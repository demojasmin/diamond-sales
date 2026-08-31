-- ---------------------------------------------------------------------------
-- Regression test for 0042 · correcting an IMPORTED sale.
--
-- The whole question this answers: when an imported invoice's figures change, does the stock move
-- by the DIFFERENCE, or by the full new amount? Moving the full amount is the double deduction
-- 0040 refused to risk, and it is silent -- the balance is simply wrong from then on.
--
-- SAFE ON A DATABASE WITH REAL STOCK. It works in a grade x size bucket nobody has ever traded,
-- everything it writes is marked 'VERIFY-0042', and its invoice is numbered MIG-VERIFY-0042 so it
-- cannot collide with a real migrated number. The cleanup matches on those alone.
--
-- IT DOES NOT RUN THE IMPORTER. replace_imported_sales rewrites every MIG- invoice in the
-- database, which is not something a test may do to a ledger somebody is using. The re-import
-- path is checked by calling clear_import_edits directly, which is the part 0042 added.
--
-- Every row of the result must read 'ok'.
-- ---------------------------------------------------------------------------

create temporary table if not exists v42 (
    grade_id bigint, size_id bigint, invoice_id bigint, opening numeric
);
delete from v42;

create temporary table if not exists v42_out (n int, check_ text, status text);
delete from v42_out;

-- ── 0 · a bucket nobody has traded, holding an "imported opening balance" ──
-- 200 ct, standing for a sheet total that is ALREADY net of the imported sale below. That is the
-- premise the whole of 0042 rests on: those carats left before this system existed.
insert into v42 (grade_id, size_id)
select g.grade_id, s.size_id
  from public.grade g
  cross join public.size_bucket s
 where g.active and s.active
   and not exists (select 1 from public.stock_movement m
                    where m.grade_id = g.grade_id and m.size_id = s.size_id)
 order by g.sort_order, s.sort_order
 limit 1;

delete from public.stock_movement where reason like 'VERIFY-0042%';

insert into public.stock_movement
    (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct, ref_type, reason)
select current_date, grade_id, size_id, 'INTAKE', 200, 1000, 'verify', 'VERIFY-0042'
  from v42;

create or replace function pg_temp.v42_bal() returns numeric language sql as $$
    select coalesce((select p.balance_ct from public.v_stock_position p
                      join v42 v on v.grade_id = p.grade_id and v.size_id = p.size_id), 0);
$$;

create or replace function pg_temp.v42_recon() returns boolean language sql as $$
    select coalesce((select r.reconciles
                       from public.v_reconciliation r
                       join v42 v on true
                       join public.grade g on g.grade_id = v.grade_id
                       join public.size_bucket s on s.size_id = v.size_id
                      where r.grade_code = g.code and r.size_code = s.code), false);
$$;

-- how many import_edit adjustments this invoice carries, and their signed total
create or replace function pg_temp.v42_adj() returns text language sql as $$
    select coalesce(count(*), 0)::text || '/' || coalesce(trim_scale(sum(weight_ct))::text, '0')
      from public.stock_movement
     where ref_type = 'import_edit' and ref_id = (select invoice_id from v42);
$$;


-- ── 1 · an imported invoice: POSTED, and carrying NO movements ─────────────
do $$
declare v record; v_inv bigint;
begin
    select * into v from v42;

    insert into public.sales_invoice
        (invoice_no, invoice_date, buyer_id, currency_id, status, doc_type, terms_days)
    values ('MIG-VERIFY-0042', current_date,
            (select buyer_id from public.buyer where active order by buyer_id limit 1),
            (select currency_id from public.currency order by currency_id limit 1),
            'POSTED', 'BILL', 30)
    returning invoice_id into v_inv;

    -- 10 ct sold. Deliberately NO stock_movement rows: that is what "imported" means.
    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct, remark)
    values (v_inv, v.grade_id, v.size_id, 10, 10, 1000, 'VERIFY-0042');

    update v42 set invoice_id = v_inv, opening = pg_temp.v42_bal();
end $$;

insert into v42_out select 1, 'IMPORT  . the sale exists and moved no stock: still 200',
       case when (select opening from v42) = 200 then 'ok'
            else 'WRONG: ' || coalesce((select opening::text from v42), 'null') end;
insert into v42_out select 2, 'IMPORT  . and carries no movements of its own',
       case when (select count(*) from public.stock_movement m
                   join public.sales_line l on l.line_id = m.ref_id and m.ref_type = 'sales_line'
                  where l.invoice_id = (select invoice_id from v42)) = 0
            then 'ok' else 'IT HAS MOVEMENTS' end;


-- ── 2 · correct it from 10 ct to 25 ct ─────────────────────────────────────
-- THE CHECK THIS FILE EXISTS FOR. 15 ct more is sold, so 15 ct more must leave: 200 - 15 = 185.
-- A balance of 175 would mean the full 25 was deducted on top of an opening balance that had
-- already taken the original 10 out.
do $$
declare v record; v_res jsonb;
begin
    select * into v from v42;
    v_res := public.edit_posted_invoice(
        v.invoice_id, current_date,
        (select buyer_id from public.buyer where active order by buyer_id limit 1),
        null, 0, 30, 'BILL',
        (select currency_id from public.currency order by currency_id limit 1),
        jsonb_build_array(jsonb_build_object(
            'grade_id', v.grade_id, 'size_id', v.size_id,
            'gross_weight_ct', 25, 'selection_ct', 25, 'price_per_ct', 1000,
            'ex_rate', 1, 'less1_pct', 0, 'less2_pct', 0, 'remark', 'VERIFY-0042')),
        'VERIFY-0042 corrected upward');

    insert into v42_out values
        (3, 'EDIT UP . an imported sale can now be corrected',
            case when v_res->>'ok' = 'true' then 'ok' else 'REFUSED' end),
        (4, 'EDIT UP . only the DIFFERENCE moves: 200 - 15 = 185',
            case when pg_temp.v42_bal() = 185 then 'ok'
                 when pg_temp.v42_bal() = 175 then 'DOUBLE DEDUCTION: took the full 25'
                 else 'WRONG: ' || pg_temp.v42_bal()::text end),
        (5, 'EDIT UP . recorded as ONE signed adjustment of -15',
            case when pg_temp.v42_adj() = '1/-15' then 'ok' else pg_temp.v42_adj() end),
        (6, 'EDIT UP . and NOT as a sale, which the report would never balance',
            case when (select count(*) from public.stock_movement m
                        join public.sales_line l on l.line_id = m.ref_id and m.ref_type = 'sales_line'
                       where l.invoice_id = v.invoice_id
                         and m.movement_type in ('SALE', 'REJECTION')) = 0
                 then 'ok' else 'IT WROTE SALE MOVEMENTS' end),
        (7, 'EDIT UP . the invoice keeps its number and stays POSTED',
            case when (select invoice_no || '/' || status from public.sales_invoice
                        where invoice_id = v.invoice_id) = 'MIG-VERIFY-0042/POSTED'
                 then 'ok'
                 else (select invoice_no || '/' || status from public.sales_invoice
                        where invoice_id = v.invoice_id) end),
        (8, 'EDIT UP . the lines were replaced, not added to',
            case when (select count(*) from public.sales_line where invoice_id = v.invoice_id) = 1
                 then 'ok'
                 else (select count(*)::text || ' lines' from public.sales_line
                        where invoice_id = v.invoice_id) end),
        (9, 'EDIT UP . the bucket still reconciles',
            case when pg_temp.v42_recon() then 'ok' else 'DOES NOT RECONCILE' end),
        (10, 'EDIT UP . imported lines stay uncostable, as the import leaves them',
            case when (select count(*) from public.sales_line
                        where invoice_id = v.invoice_id and cost_per_ct is not null) = 0
                 then 'ok' else 'A COST WAS STAMPED' end);
end $$;


-- ── 3 · correct it downward, 25 ct to 5 ct ─────────────────────────────────
-- Against the ORIGINAL 10, selling 5 means 5 ct never left: 200 + 5 = 205.
do $$
declare v record;
begin
    select * into v from v42;
    perform public.edit_posted_invoice(
        v.invoice_id, current_date,
        (select buyer_id from public.buyer where active order by buyer_id limit 1),
        null, 0, 30, 'BILL',
        (select currency_id from public.currency order by currency_id limit 1),
        jsonb_build_array(jsonb_build_object(
            'grade_id', v.grade_id, 'size_id', v.size_id,
            'gross_weight_ct', 5, 'selection_ct', 5, 'price_per_ct', 1000,
            'ex_rate', 1, 'less1_pct', 0, 'less2_pct', 0, 'remark', 'VERIFY-0042')),
        'VERIFY-0042 corrected downward');
end $$;

insert into v42_out select 11, 'EDIT DN . selling less gives carats back: 200 + 5 = 205',
       case when pg_temp.v42_bal() = 205 then 'ok' else 'WRONG: ' || pg_temp.v42_bal()::text end;
-- Both adjustments stay on the ledger. -15 then +20 nets to +5, which is the whole correction
-- against the original 10 ct. History is added to, never rewritten.
insert into v42_out select 12, 'EDIT DN . both corrections are on the ledger, netting +5',
       case when pg_temp.v42_adj() = '2/5' then 'ok' else pg_temp.v42_adj() end;
insert into v42_out select 13, 'EDIT DN . and the bucket still reconciles',
       case when pg_temp.v42_recon() then 'ok' else 'DOES NOT RECONCILE' end;


-- ── 4 · what must still be refused ─────────────────────────────────────────
do $$
declare v record; v_before numeric; v_blocked boolean; v_ok boolean;
begin
    select * into v from v42;
    v_before := pg_temp.v42_bal();

    -- More than the bucket holds.
    begin
        perform public.edit_posted_invoice(
            v.invoice_id, current_date,
            (select buyer_id from public.buyer where active order by buyer_id limit 1),
            null, 0, 30, 'BILL',
            (select currency_id from public.currency order by currency_id limit 1),
            jsonb_build_array(jsonb_build_object(
                'grade_id', v.grade_id, 'size_id', v.size_id,
                'gross_weight_ct', 99999, 'selection_ct', 99999, 'price_per_ct', 1000)),
            'VERIFY-0042 far too much');
        v_blocked := false;
    exception when others then v_blocked := true;
    end;

    insert into v42_out values
        (14, 'GUARD   . a correction that would go negative is refused',
             case when v_blocked then 'ok' else 'WRONGLY ALLOWED' end),
        (15, 'GUARD   . and it ROLLED BACK: stock and lines untouched',
             case when pg_temp.v42_bal() = v_before
                   and (select sum(gross_weight_ct) from public.sales_line
                         where invoice_id = v.invoice_id) = 5
                  then 'ok'
                  else 'stock ' || v_before::text || ' -> ' || pg_temp.v42_bal()::text end);

    -- No reason.
    begin
        perform public.edit_posted_invoice(
            v.invoice_id, current_date,
            (select buyer_id from public.buyer where active order by buyer_id limit 1),
            null, 0, 30, 'BILL',
            (select currency_id from public.currency order by currency_id limit 1),
            jsonb_build_array(jsonb_build_object(
                'grade_id', v.grade_id, 'size_id', v.size_id,
                'gross_weight_ct', 6, 'selection_ct', 6, 'price_per_ct', 1000)),
            '   ');
        v_ok := false;
    exception when others then v_ok := true;
    end;
    insert into v42_out values (16, 'GUARD   . an unexplained correction is still refused',
                                case when v_ok then 'ok' else 'WRONGLY ALLOWED' end);
end $$;


-- ── 5 · a re-import takes the correction with it ───────────────────────────
-- Not by running the importer -- that would rewrite every MIG- invoice in the database -- but by
-- calling the part 0042 added to it. Without this, restoring the sheet's original lines would
-- leave stock holding a correction whose invoice had been replaced.
do $$
declare v record; v_cleared integer; v_after numeric;
begin
    select * into v from v42;
    v_cleared := public.clear_import_edits(array[v.invoice_id]);
    v_after := pg_temp.v42_bal();

    insert into v42_out values
        (17, 'REIMPORT. clearing the edits removes both adjustments',
             case when v_cleared = 2 then 'ok' else v_cleared::text || ' removed, expected 2' end),
        (18, 'REIMPORT. and the bucket returns to its imported opening balance',
             case when v_after = 200 then 'ok' else 'WRONG: ' || v_after::text end),
        (19, 'REIMPORT. the importer calls it, so a re-import cannot strand one',
             case when pg_get_functiondef(
                        (select p.oid from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                          where n.nspname = 'public' and p.proname = 'replace_imported_sales'
                          limit 1)) like '%clear_import_edits%'
                  then 'ok' else 'THE IMPORTER DOES NOT CLEAR THEM' end);
end $$;


-- ── 6 · and the database as a whole ────────────────────────────────────────
insert into v42_out select 20, 'ALL     . no bucket anywhere is negative',
       case when (select count(*) from public.v_stock_position where balance_ct < 0) = 0
            then 'ok' else 'NEGATIVE BUCKETS PRESENT' end;
insert into v42_out select 21, 'ALL     . every bucket in the database reconciles',
       case when (select count(*) from public.v_reconciliation where not reconciles) = 0
            then 'ok'
            else (select string_agg(grade_code || ' x ' || size_code
                                    || ' (' || trim_scale(diff_ct) || ' ct)', ', ')
                    from public.v_reconciliation where not reconciles) end;
insert into v42_out select 22, 'ALL     . no import_edit adjustment is orphaned',
       case when (select count(*) from public.stock_movement m
                   where m.ref_type = 'import_edit'
                     and not exists (select 1 from public.sales_invoice i
                                      where i.invoice_id = m.ref_id)) = 0
            then 'ok' else 'ORPHANED ADJUSTMENTS PRESENT' end;


-- ── 7 · the answer, then remove everything this script created ─────────────
select check_ as check, status from v42_out order by n;

delete from public.stock_movement
 where ref_type = 'import_edit' and ref_id in (select invoice_id from v42);
delete from public.stock_movement
 where ref_type = 'sales_line'
   and ref_id in (select line_id from public.sales_line
                   where invoice_id in (select invoice_id from v42));
delete from public.sales_line   where invoice_id in (select invoice_id from v42);
delete from public.sales_invoice where invoice_no = 'MIG-VERIFY-0042';
delete from public.stock_movement where reason like 'VERIFY-0042%';

drop table if exists v42_out;
drop table if exists v42;
