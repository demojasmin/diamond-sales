-- ---------------------------------------------------------------------------
-- Is this database sound? Nine questions, no writes.
--
-- STRICTLY READ ONLY. Every statement is a SELECT against the catalogue and the
-- ledger. No table is written, no function is called, no row is created or
-- removed, and there is no transaction to roll back because there is nothing to
-- undo. Safe on the client's live database, during trading hours.
--
-- This is the one to run on PRIYA. The other two verification scripts --
-- TEST_import_stock_deduct.sql and VERIFY_STOCK_SALES_RECONCILIATION.sql -- both
-- WRITE. They clean up after themselves and the first is wrapped in a rollback,
-- which is honest enough on Demo, but the first act of replace_imported_sales is
-- to delete every MIG- invoice and the rollback is the only thing that puts them
-- back. That is not a risk to take on a database somebody is running a business
-- on.
--
-- JUST PRESS RUN. The editor shows only the last statement's result, and the last
-- statement here is a SUMMARY of all nine as one table -- so running the whole
-- file answers everything at once.
--
-- The nine blocks above it are the same questions in full detail, for when a
-- summary line needs opening up: block 3 names each row and what it carries,
-- block 5 lists the accounts. Select a block and press Run to see one on its own.
-- ---------------------------------------------------------------------------


-- ── 1 · which build of the schema is installed ─────────────────────────────
-- The sales importer is the one that has moved four times; its comment is the
-- only thing that says which version is really there.
select
    coalesce(left(obj_description('public.replace_imported_sales'::regproc), 4), '(none)')
        as sales_importer,
    case when pg_get_viewdef('public.v_reconciliation'::regclass) like '%MIG-%%'
         then 'pre-0052' else '0052' end                        as reconciliation,
    case when pg_get_functiondef('public.import_stock'::regproc) ilike '%would be left below zero%'
         then 'pre-0047 (refuses a negative position)' else '0047' end as stock_import,
    (select value from public.app_config where key = 'company_name') as company;


-- ── 2 · the catalogue, and anything doubled in it ──────────────────────────
-- A duplicate CODE is the fault that put two "GH VS" rows in every picker: the
-- app shows marks, the database stores codes, and an import that cannot resolve
-- a mark offers to create the code it was given.
select 'grades' as thing, count(*)::text as count from public.grade
union all select 'sizes',    count(*)::text from public.size_bucket
union all select 'pairings', count(*)::text from public.grade_size
union all select 'grades sharing a sort_order',
       coalesce((select string_agg(codes, ' | ') from (
           select string_agg(code, ', ') as codes from public.grade
            group by sort_order having count(*) > 1) d), 'none')
union all select 'grade codes that differ only by spacing/case',
       coalesce((select string_agg(codes, ' | ') from (
           select string_agg(code, ', ') as codes from public.grade
            group by upper(replace(code, ' ', '')) having count(*) > 1) d), 'none');


-- ── 3 · rows that should not be sold from, and whether they hold trade ─────
-- Hidden from the sale pickers by the app (Catalogue.NotForSale). Listed here
-- because "hidden" and "gone" are different, and only one of them is safe to
-- assume.
select g.code, 'grade' as kind, g.active,
       (select count(*) from public.sales_line l where l.grade_id = g.grade_id)     as sales_lines,
       (select count(*) from public.stock_movement m where m.grade_id = g.grade_id) as movements
  from public.grade g
 where g.code in ('ZZ TEST', 'Unknown Grade', '+14', 'GH VS')
union all
select z.code, 'size', z.active,
       (select count(*) from public.sales_line l where l.size_id = z.size_id),
       (select count(*) from public.stock_movement m where m.size_id = z.size_id)
  from public.size_bucket z
 where z.code = '14+'
 order by 1;


-- ── 4 · nothing may be left unreachable ────────────────────────────────────
-- An active grade paired with no sieve is in every picker and picks nothing,
-- which reads as a broken screen rather than as a catalogue with a gap.
select 'active grade with no sizes' as fault, g.code as which
  from public.grade g
 where g.active and not exists (select 1 from public.grade_size s where s.grade_id = g.grade_id)
union all
select 'active size no grade trades', z.code
  from public.size_bucket z
 where z.active and not exists (select 1 from public.grade_size s where s.size_id = z.size_id)
 order by 1, 2;


-- ── 5 · who can sign in ────────────────────────────────────────────────────
select id, full_name, role, active from public.profiles order by role, full_name;


-- ── 6 · how much is in here ────────────────────────────────────────────────
select 'invoices (all)'      as thing, count(*)::text as count from public.sales_invoice
union all select 'invoices imported (MIG-)', count(*)::text from public.sales_invoice
    where invoice_no like 'MIG-%'
union all select 'invoices cancelled', count(*)::text from public.sales_invoice
    where status = 'CANCELLED'
union all select 'sales lines',     count(*)::text from public.sales_line
union all select 'receipts',        count(*)::text from public.receipt
union all select 'stock movements', count(*)::text from public.stock_movement
union all select 'reservations held (should be 0 when nobody is typing)',
    count(*)::text from public.stock_reservation;


-- ── 6b · what an import would replace, in carats ───────────────────────────
-- Imported sales that have never moved stock. Non-zero on a database whose
-- importer predates 0048: the invoices exist, the ledger never heard of them,
-- and the next import under 0051 will deduct all of it at once.
select coalesce(sum(l.selection_ct), 0) as mig_selection_ct,
       coalesce(sum(m.moved), 0)        as mig_moved_ct
  from public.sales_line l
  join public.sales_invoice i on i.invoice_id = l.invoice_id
  left join lateral (
      select sum(x.weight_ct) as moved from public.stock_movement x
       where x.ref_type = 'sales_line' and x.ref_id = l.line_id
  ) m on true
 where i.invoice_no like 'MIG-%' and i.status = 'POSTED';


-- ── 7 · the stock position ─────────────────────────────────────────────────
select round(sum(balance_ct), 4)                        as available_ct,
       round(sum(ledger_ct), 4)                         as on_hand_ct,
       round(sum(reserved_ct), 4)                       as reserved_ct,
       count(*) filter (where balance_ct > 0)           as buckets_holding,
       count(*) filter (where balance_ct < 0)           as buckets_negative
  from public.v_stock_position;


-- ── 8 · does the ledger agree with the invoices ────────────────────────────
-- Under 0052 a clean database reads 0. A negative total is the SELECTION that an
-- import could not take out because the shelf did not hold it (0051) -- true, and
-- the same figure the import dialog reported as part of short_ct.
select count(*) filter (where not reconciles)                      as buckets_off,
       coalesce(round(sum(diff_ct) filter (where not reconciles), 4), 0) as ct_off
  from public.v_reconciliation;


-- ── 9 · movements pointing at nothing ──────────────────────────────────────
-- A SALE or REJECTION whose sales line has been deleted deducts carats for an
-- invoice that no longer exists. Must be 0.
select count(*) as orphaned_sale_movements
  from public.stock_movement m
 where m.ref_type = 'sales_line'
   and m.movement_type in ('SALE', 'REJECTION')
   and not exists (select 1 from public.sales_line l where l.line_id = m.ref_id);


-- ═══════════════════════════════════════════════════════════════════════════
-- ALL NINE, AS ONE TABLE. The last statement in the file, so pressing Run on the
-- whole thing lands here. Everything is text because the answers are of every
-- shape -- a version, a count, a carat weight, a list of codes.
--
-- The "expected" column says what a sound database reads, so a wrong answer does
-- not need this file open beside it to be recognised.
-- ═══════════════════════════════════════════════════════════════════════════
with q as (
-- "item", not "check": check is a reserved word, and while it survives as an
-- alias it cannot be named in the select list at the bottom.
select 1 as ord, 'company' as item,
       coalesce((select value from public.app_config where key = 'company_name'), '(none)') as result,
       'Priya Sales, or Priya Gems on Demo' as expected

union all select 2, 'sales importer version',
       coalesce(left(obj_description('public.replace_imported_sales'::regproc), 4), '(no comment - older than 0048)'),
       '0051'

union all select 3, 'reconciliation view',
       case when pg_get_viewdef('public.v_reconciliation'::regclass) like '%MIG-%%'
            then 'pre-0052' else '0052' end,
       '0052'

union all select 4, 'stock import',
       case when pg_get_functiondef('public.import_stock'::regproc) ilike '%would be left below zero%'
            then 'pre-0047 - refuses a negative position' else '0047' end,
       '0047'

union all select 5, 'grades / sizes / pairings',
       (select count(*) from public.grade)::text || ' / ' ||
       (select count(*) from public.size_bucket)::text || ' / ' ||
       (select count(*) from public.grade_size)::text,
       'no target'

union all select 6, 'grades sharing a sort_order',
       coalesce((select string_agg(codes, ' | ') from (
           select string_agg(code, ', ') as codes from public.grade
            group by sort_order having count(*) > 1) d), 'none'),
       'none'

union all select 7, 'near-duplicate grade codes',
       coalesce((select string_agg(codes, ' | ') from (
           select string_agg(code, ', ') as codes from public.grade
            group by upper(replace(code, ' ', '')) having count(*) > 1) d), 'none'),
       -- NOT the check that catches a second "GH VS": that row's code differs from
       -- "GH" by more than spacing, so it is a different code and this passes it.
       -- Row 8 is where a stray GH VS shows up.
       'none'

union all select 8, 'not-for-sale rows still present',
       coalesce((select string_agg(code, ', ' order by code) from public.grade
                  where code in ('ZZ TEST', 'Unknown Grade', '+14', 'GH VS')), 'none')
       || coalesce((select ' | size ' || code from public.size_bucket where code = '14+'), ''),
       'Unknown Grade is load-bearing; the rest depend on the desk'

union all select 9, 'active grades with no sieve',
       coalesce((select string_agg(g.code, ', ' order by g.code) from public.grade g
                  where g.active and not exists
                        (select 1 from public.grade_size s where s.grade_id = g.grade_id)), 'none'),
       'none'

union all select 10, 'accounts (active / total)',
       (select count(*) filter (where active) from public.profiles)::text || ' / ' ||
       (select count(*) from public.profiles)::text,
       'no target - block 5 names them'

union all select 11, 'invoices (all / imported / cancelled)',
       (select count(*) from public.sales_invoice)::text || ' / ' ||
       (select count(*) from public.sales_invoice where invoice_no like 'MIG-%')::text || ' / ' ||
       (select count(*) from public.sales_invoice where status = 'CANCELLED')::text,
       'no target'

union all select 12, 'lines / receipts / movements',
       (select count(*) from public.sales_line)::text || ' / ' ||
       (select count(*) from public.receipt)::text || ' / ' ||
       (select count(*) from public.stock_movement)::text,
       'no target'

union all select 13, 'reservations held right now',
       (select count(*) from public.stock_reservation)::text,
       '0 when nobody is mid-entry'

union all select 14, 'imported sales: sold ct / taken out of stock ct',
       (select coalesce(round(sum(l.selection_ct), 4), 0)::text || ' / ' ||
               coalesce(round(sum(m.moved), 4), 0)::text
          from public.sales_line l
          join public.sales_invoice i on i.invoice_id = l.invoice_id
          left join lateral (select sum(x.weight_ct) as moved from public.stock_movement x
                              where x.ref_type = 'sales_line' and x.ref_id = l.line_id) m on true
         where i.invoice_no like 'MIG-%' and i.status = 'POSTED'),
       'a second number of 0 means imported sales have never moved stock'

union all select 15, 'stock: available / on hand / reserved ct',
       (select round(sum(balance_ct), 4)::text || ' / ' ||
               round(sum(ledger_ct), 4)::text || ' / ' ||
               round(sum(reserved_ct), 4)::text from public.v_stock_position),
       'available = on hand less reserved'

union all select 16, 'buckets holding / negative',
       (select count(*) filter (where balance_ct > 0)::text || ' / ' ||
               count(*) filter (where balance_ct < 0)::text from public.v_stock_position),
       'negative is allowed (0047) but worth knowing'

union all select 17, 'reconciliation: buckets off / ct off',
       (select count(*) filter (where not reconciles)::text || ' / ' ||
               coalesce(round(sum(diff_ct) filter (where not reconciles), 4), 0)::text
          from public.v_reconciliation),
       '0 / 0, or the selection an import could not take out'

union all select 18, 'orphaned sale movements',
       (select count(*)::text from public.stock_movement m
         where m.ref_type = 'sales_line' and m.movement_type in ('SALE', 'REJECTION')
           and not exists (select 1 from public.sales_line l where l.line_id = m.ref_id)),
       '0 - anything else is carats deducted for a deleted invoice'
)
select item, result, expected from q order by ord;
