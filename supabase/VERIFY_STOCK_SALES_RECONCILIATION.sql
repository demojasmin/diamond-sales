-- ---------------------------------------------------------------------------
-- Stock <-> Sales, end to end: ADD, UPDATE, CANCEL, IMPORT.
--
-- One bucket that has never traded, taken through every operation that can move it, checking
-- after each that the balance is right, that no movement is duplicated or orphaned, and that
-- v_reconciliation still agrees with the invoices.
--
-- SAFE ON A DATABASE WITH REAL STOCK. Everything written is marked 'VERIFY-RECON' and the
-- cleanup matches on that alone. The import step is deliberately an APPEND, never a replace:
-- a replacing import would rewrite the whole stock sheet, which is not something a test may do
-- to a database somebody is using.
--
-- Every row of the result must read 'ok'.
-- ---------------------------------------------------------------------------

create temporary table if not exists vr (grade_id bigint, size_id bigint, invoice_id bigint, no text);
delete from vr;

create temporary table if not exists vr_out (n int, check_ text, status text);
delete from vr_out;

insert into vr (grade_id, size_id)
select g.grade_id, s.size_id
  from public.grade g
  cross join public.size_bucket s
 where g.active and s.active
   and not exists (select 1 from public.stock_movement m
                    where m.grade_id = g.grade_id and m.size_id = s.size_id)
 order by g.sort_order, s.sort_order
 limit 1;

delete from public.stock_movement where reason like 'VERIFY-RECON%';

-- Helper: this bucket's balance, and whether it reconciles.
create or replace function pg_temp.vr_bal() returns numeric language sql as $$
    select coalesce((select p.balance_ct from public.v_stock_position p
                      join vr v on v.grade_id = p.grade_id and v.size_id = p.size_id), 0);
$$;

create or replace function pg_temp.vr_recon() returns boolean language sql as $$
    select coalesce((select r.reconciles
                       from public.v_reconciliation r
                       join vr v on true
                       join public.grade g on g.grade_id = v.grade_id
                       join public.size_bucket s on s.size_id = v.size_id
                      where r.grade_code = g.code and r.size_code = s.code), false);
$$;

-- Helper: how many live SALE movements this bucket's invoice has, and their total.
create or replace function pg_temp.vr_sales(text) returns text language sql as $$
    select coalesce(count(*), 0)::text || '/' || coalesce(trim_scale(sum(m.weight_ct))::text, '0')
      from public.stock_movement m
      join public.sales_line l on l.line_id = m.ref_id and m.ref_type = 'sales_line'
     where m.movement_type = $1
       and l.invoice_id = (select invoice_id from vr);
$$;


-- ── 0 · 200 ct arrives ─────────────────────────────────────────────────────
insert into public.stock_movement
    (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct, ref_type, reason)
select current_date, grade_id, size_id, 'INTAKE', 200, 1000, 'verify', 'VERIFY-RECON'
  from vr;

insert into vr_out select 1, 'INTAKE  · 200 ct in',
       case when pg_temp.vr_bal() = 200 then 'ok' else 'WRONG: ' || pg_temp.vr_bal()::text end;


-- ── 1 · ADD · a sale of 30 ct (25 selection, 5 rejection) ──────────────────
do $$
declare v record; v_inv bigint;
begin
    select * into v from vr;
    insert into public.sales_invoice (invoice_date, buyer_id, currency_id, status, doc_type, terms_days)
    values (current_date,
            (select buyer_id from public.buyer where active order by buyer_id limit 1),
            (select currency_id from public.currency order by currency_id limit 1),
            'DRAFT', 'BILL', 30)
    returning invoice_id into v_inv;

    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct, remark)
    values (v_inv, v.grade_id, v.size_id, 30, 25, 1000, 'VERIFY-RECON');

    perform public.post_invoice(v_inv, false);
    update vr set invoice_id = v_inv,
                  no = (select invoice_no from public.sales_invoice where invoice_id = v_inv);
end $$;

insert into vr_out select 2, 'ADD     · the whole parcel leaves: 200 - 30 = 170',
       case when pg_temp.vr_bal() = 170 then 'ok' else 'WRONG: ' || pg_temp.vr_bal()::text end;
insert into vr_out select 3, 'ADD     · one SALE of 25, one REJECTION of 5',
       case when pg_temp.vr_sales('SALE') = '1/25' and pg_temp.vr_sales('REJECTION') = '1/5'
            then 'ok' else pg_temp.vr_sales('SALE') || ' + ' || pg_temp.vr_sales('REJECTION') end;
insert into vr_out select 4, 'ADD     · the bucket reconciles',
       case when pg_temp.vr_recon() then 'ok' else 'DOES NOT RECONCILE' end;


-- ── 2 · UPDATE · the same invoice corrected to 60 ct (50 / 10) ─────────────
do $$
declare v record;
begin
    select * into v from vr;
    perform public.edit_posted_invoice(
        v.invoice_id, current_date,
        (select buyer_id from public.buyer where active order by buyer_id limit 1),
        null, 0, 30, 'BILL',
        (select currency_id from public.currency order by currency_id limit 1),
        jsonb_build_array(jsonb_build_object(
            'grade_id', v.grade_id, 'size_id', v.size_id,
            'gross_weight_ct', 60, 'selection_ct', 50, 'price_per_ct', 1000,
            'ex_rate', 1, 'less1_pct', 0, 'less2_pct', 0, 'remark', 'VERIFY-RECON')),
        'VERIFY-RECON corrected');
end $$;

insert into vr_out select 5, 'UPDATE  · stock follows the new figure: 200 - 60 = 140',
       case when pg_temp.vr_bal() = 140 then 'ok' else 'WRONG: ' || pg_temp.vr_bal()::text end;
insert into vr_out select 6, 'UPDATE  · NO duplicate movements: still one SALE, one REJECTION',
       case when pg_temp.vr_sales('SALE') = '1/50' and pg_temp.vr_sales('REJECTION') = '1/10'
            then 'ok' else pg_temp.vr_sales('SALE') || ' + ' || pg_temp.vr_sales('REJECTION') end;
insert into vr_out select 7, 'UPDATE  · the bucket still reconciles',
       case when pg_temp.vr_recon() then 'ok' else 'DOES NOT RECONCILE' end;


-- ── 3 · CANCEL · the stock comes back, and the report stays honest ─────────
do $$
declare v record;
begin
    select * into v from vr;
    perform public.cancel_invoice(v.invoice_id, 'VERIFY-RECON cancelled');
end $$;

insert into vr_out select 8, 'CANCEL  · every carat returns: back to 200',
       case when pg_temp.vr_bal() = 200 then 'ok' else 'WRONG: ' || pg_temp.vr_bal()::text end;
insert into vr_out select 9, 'CANCEL  · the reversal is on the ledger, not a deletion',
       case when (select count(*) from public.stock_movement m
                   where m.ref_type = 'cancel' and m.ref_id = (select invoice_id from vr)) = 2
            then 'ok'
            else (select count(*)::text from public.stock_movement m
                   where m.ref_type = 'cancel' and m.ref_id = (select invoice_id from vr)) end;
-- 0041. Before it, this was the one that failed: the SALE stayed in moved_out_ct while the
-- cancelled line left sold_on_invoices_ct, so the bucket reported a difference for ever.
insert into vr_out select 10, 'CANCEL  · the bucket STILL reconciles  (0041)',
       case when pg_temp.vr_recon() then 'ok' else 'DOES NOT RECONCILE - is 0041 applied?' end;


-- ── 4 · IMPORT · an appending stock import adds, and touches no sale ───────
do $$
declare v record;
begin
    select * into v from vr;
    -- p_replace => FALSE. A replacing import would rewrite the whole sheet.
    perform public.import_stock(
        current_date,
        jsonb_build_array(jsonb_build_object(
            'grade_id', v.grade_id, 'size_id', v.size_id,
            'weight_ct', 40, 'price_per_ct', 1000)),
        false, null, 'pdf');
end $$;

insert into vr_out select 11, 'IMPORT  · 40 ct added: 200 + 40 = 240',
       case when pg_temp.vr_bal() = 240 then 'ok' else 'WRONG: ' || pg_temp.vr_bal()::text end;
insert into vr_out select 12, 'IMPORT  · it wrote no sale and moved no invoice',
       case when pg_temp.vr_sales('SALE') = '1/50' then 'ok' else pg_temp.vr_sales('SALE') end;
insert into vr_out select 13, 'IMPORT  · the bucket still reconciles',
       case when pg_temp.vr_recon() then 'ok' else 'DOES NOT RECONCILE' end;


-- ── 5 · MIGRATED · an imported sale is counted on BOTH sides  (0052) ───────
-- Written straight to the tables rather than through replace_imported_sales, which DELETES every
-- MIG- invoice as its first act -- not something a test may do to a database somebody is using.
-- The rows are exactly what 0051 writes: a POSTED MIG- invoice, one line, one SALE movement
-- against it for what the line sold.
--
-- This is the check that separates 0052 from 0041. Under 0041 the movement was counted in
-- moved_out_ct while the line was excluded from sold_on_invoices_ct, so the bucket reported a
-- 20 ct difference that no trading could clear. Under 0052 both sides see it and it cancels.
do $$
declare v record; v_inv bigint; v_line bigint;
begin
    select * into v from vr;
    insert into public.sales_invoice
        (invoice_no, invoice_date, buyer_id, currency_id, status, doc_type, terms_days)
    values ('MIG-VERIFY-RECON', current_date,
            (select buyer_id from public.buyer where active order by buyer_id limit 1),
            (select currency_id from public.currency order by currency_id limit 1),
            'POSTED', 'BILL', 0)
    returning invoice_id into v_inv;

    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct, remark)
    values (v_inv, v.grade_id, v.size_id, 20, 20, 1000, 'VERIFY-RECON')
    returning line_id into v_line;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, reason)
    values (current_date, v.grade_id, v.size_id, 'SALE', 20,
            1000, 'sales_line', v_line, 'VERIFY-RECON MIG');
end $$;

insert into vr_out select 14, 'MIGRATED· the imported sale leaves stock: 240 - 20 = 220',
       case when pg_temp.vr_bal() = 220 then 'ok' else 'WRONG: ' || pg_temp.vr_bal()::text end;
insert into vr_out select 15, 'MIGRATED· and the bucket RECONCILES  (0052; fails under 0041)',
       case when pg_temp.vr_recon() then 'ok'
            else 'DOES NOT RECONCILE - is 0052 applied? 0041 excluded MIG- lines from the sold side' end;

-- Gone before the whole-database checks below, so a leftover test invoice cannot make them read
-- worse than the database really is.
delete from public.stock_movement
 where ref_type = 'sales_line'
   and ref_id in (select line_id from public.sales_line l
                    join public.sales_invoice i using (invoice_id)
                   where i.invoice_no = 'MIG-VERIFY-RECON');
delete from public.sales_line
 where invoice_id in (select invoice_id from public.sales_invoice
                       where invoice_no = 'MIG-VERIFY-RECON');
delete from public.sales_invoice where invoice_no = 'MIG-VERIFY-RECON';


-- ── 6 · and the whole database, not just this bucket ──────────────────────
insert into vr_out select 16, 'ALL     · no bucket anywhere is negative',
       case when (select count(*) from public.v_stock_position where balance_ct < 0) = 0
            then 'ok' else 'NEGATIVE BUCKETS PRESENT' end;
insert into vr_out select 17, 'ALL     · every bucket in the database reconciles',
       case when (select count(*) from public.v_reconciliation where not reconciles) = 0
            then 'ok'
            else (select string_agg(grade_code || ' x ' || size_code
                                    || ' (' || trim_scale(diff_ct) || ' ct)', ', ')
                    from public.v_reconciliation where not reconciles) end;
insert into vr_out select 18, 'ALL     · no SALE movement points at a line that is gone',
       case when (select count(*) from public.stock_movement m
                   where m.movement_type in ('SALE', 'REJECTION')
                     and m.ref_type = 'sales_line'
                     and not exists (select 1 from public.sales_line l where l.line_id = m.ref_id)) = 0
            then 'ok'
            else (select count(*)::text || ' orphaned movement(s)' from public.stock_movement m
                   where m.movement_type in ('SALE', 'REJECTION')
                     and m.ref_type = 'sales_line'
                     and not exists (select 1 from public.sales_line l where l.line_id = m.ref_id)) end;


-- ── 7 · the answer, then remove everything this script created ─────────────
select check_ as check, status from vr_out order by n;

delete from public.stock_movement
 where ref_type in ('sales_line', 'cancel')
   and (ref_id in (select line_id from public.sales_line
                    where invoice_id in (select invoice_id from vr))
        or ref_id in (select invoice_id from vr));
delete from public.sales_line   where invoice_id in (select invoice_id from vr);
delete from public.sales_invoice where invoice_id in (select invoice_id from vr);
delete from public.stock_movement
 where ref_type = 'stock_import'
   and grade_id in (select grade_id from vr) and size_id in (select size_id from vr);
delete from public.rough_intake
 where import_source = 'pdf' and intake_date = current_date
   and grade_id in (select grade_id from vr) and size_id in (select size_id from vr);
delete from public.stock_movement where reason like 'VERIFY-RECON%';

drop table if exists vr_out;
drop table if exists vr;
