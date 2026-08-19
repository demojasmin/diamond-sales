-- ---------------------------------------------------------------------------
-- READ ONLY. Lists what LOOKS like test data. Deletes nothing, changes nothing.
--
-- Run it, read it, decide. Nothing here is safe to delete on the strength of its
-- name alone -- a buyer called "ABC Company" in the other database turned out to
-- carry 186 invoices and 758 million rupees of real trade. Names are a hint;
-- usage is the evidence, so every section below reports what a row is ATTACHED
-- to, not just that it exists.
--
-- WHAT COUNTS AS REAL HERE
--
--   MIG- invoices, their lines and receipts   the migrated workbook history
--   buyers, brokers, grades, sizes, settings  the catalogue
--   INTAKE movements from a stock import      the position as last counted
--
-- Everything else was typed into the app by somebody, which during a week of
-- testing usually means us.
-- ---------------------------------------------------------------------------

\echo '=== 1 · movements typed in by hand (not from an import or an invoice) ==='
select m.movement_type,
       m.ref_type,
       count(*)                                as rows,
       round(sum(m.weight_ct), 4)              as carats,
       min(m.movement_date)                    as earliest,
       max(m.movement_date)                    as latest,
       count(*) filter (where m.reason is not null) as with_a_reason
  from public.stock_movement m
 where m.ref_type in ('manual', 'cancel')
 group by m.movement_type, m.ref_type
 order by 1, 2;

\echo ''
\echo '=== 2 · every hand-typed movement, in full ==='
select m.movement_id, m.movement_date, g.code as grade, s.code as size,
       m.movement_type, m.weight_ct, m.price_per_ct,
       coalesce(p.full_name, '(unknown)') as entered_by,
       left(coalesce(m.reason, ''), 60)   as reason
  from public.stock_movement m
  join public.grade g       on g.grade_id = m.grade_id
  join public.size_bucket s on s.size_id  = m.size_id
  left join public.profiles p on p.id = m.created_by
 where m.ref_type in ('manual', 'cancel')
 order by m.movement_id;

\echo ''
\echo '=== 3 · rejection dispositions (only ever created by hand) ==='
select d.disposition_id, d.movement_id, d.weight_ct,
       g.code as sorted_into, left(coalesce(d.note, ''), 50) as note
  from public.rejection_disposition d
  left join public.grade g on g.grade_id = d.to_grade_id
 order by d.disposition_id;

\echo ''
\echo '=== 4 · invoices that did NOT come from the workbook ==='
select i.invoice_id, i.invoice_no, i.invoice_date, i.status,
       b.name as buyer,
       (select count(*) from public.sales_line l where l.invoice_id = i.invoice_id) as lines,
       (select count(*) from public.receipt  r where r.invoice_id = i.invoice_id)   as receipts
  from public.sales_invoice i
  join public.buyer b on b.buyer_id = i.buyer_id
 where i.invoice_no is null or i.invoice_no not like 'MIG-%'
 order by i.invoice_id;

\echo ''
\echo '=== 5 · receipts not marked IMPORTED ==='
select r.receipt_id, r.invoice_id, i.invoice_no, r.receipt_date, r.amount, r.method
  from public.receipt r
  join public.sales_invoice i on i.invoice_id = r.invoice_id
 where r.method is distinct from 'IMPORTED'
 order by r.receipt_id;

\echo ''
\echo '=== 6 · buckets showing NEGATIVE stock ==='
-- Not deletable on their own: a negative balance is the SUM of movements, so it
-- is fixed by removing or reversing whatever took it below zero, which sections
-- 1 and 2 above will be holding.
select grade_code, size_code, balance_ct
  from public.v_stock_position
 where balance_ct < 0
 order by balance_ct;

\echo ''
\echo '=== 7 · stock imports currently standing ==='
select batch_id, source, as_at, parcels, carats, value
  from public.v_stock_import_batch
 order by last_intake_id;

\echo ''
\echo '=== 8 · master data with a test-sounding name -- CHECK USAGE BEFORE BELIEVING IT ==='
select 'buyer' as kind, b.name,
       (select count(*) from public.sales_invoice i where i.buyer_id = b.buyer_id) as used_on_invoices
  from public.buyer b
 where b.name ~* '(test|demo|abc|xxx|sample|dummy|asdf|qwer)'
union all
select 'broker', br.name,
       (select count(*) from public.sales_invoice i where i.broker_id = br.broker_id)
  from public.broker br
 where br.name ~* '(test|demo|abc|xxx|sample|dummy|asdf|qwer)'
 order by 1, 2;

\echo ''
\echo '=== 9 · grades beyond the seeded catalogue ==='
select g.grade_id, g.code, g.display_name, g.active,
       (select count(*) from public.stock_movement m where m.grade_id = g.grade_id) as movements,
       (select count(*) from public.sales_line   l where l.grade_id = g.grade_id)   as sales_lines
  from public.grade g
 where g.grade_id > 27
 order by g.grade_id;

\echo ''
\echo '=== 10 · leftover lockout rows ==='
select email, fails, last_fail_at, locked_until from public.login_attempt order by email;

\echo ''
\echo '=== 11 · totals, so nothing above is read out of proportion ==='
select 'invoices'        as t, count(*)::text as v from public.sales_invoice
union all select 'of which MIG-', count(*)::text from public.sales_invoice where invoice_no like 'MIG-%'
union all select 'sales lines',   count(*)::text from public.sales_line
union all select 'receipts',      count(*)::text from public.receipt
union all select 'movements',     count(*)::text from public.stock_movement
union all select 'stock carats',  coalesce(round(sum(balance_ct),4)::text,'-') from public.v_stock_position
union all select 'audit rows',    count(*)::text from public.audit_log;
