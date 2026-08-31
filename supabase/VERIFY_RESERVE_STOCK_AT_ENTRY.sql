-- ---------------------------------------------------------------------------
-- Regression test for 0043 · stock falls as a line is entered.
--
-- The four things that were asked for, and one that was not asked for but decides whether the
-- others are safe: that confirming does not subtract the same carats a second time.
--
-- SAFE ON A DATABASE WITH REAL STOCK. It works in a grade x size bucket nobody has ever traded,
-- everything it writes is marked 'VERIFY-0043' or belongs to its own client_ref, and the cleanup
-- matches on those alone. Run it on Demo first.
--
-- Every row of the result must read 'ok'.
-- ---------------------------------------------------------------------------

create temporary table if not exists v43 (
    grade_id bigint, size_id bigint, ref uuid, invoice_id bigint
);
delete from v43;

create temporary table if not exists v43_out (n int, check_ text, status text);
delete from v43_out;

insert into v43 (grade_id, size_id, ref)
select g.grade_id, s.size_id, gen_random_uuid()
  from public.grade g
  cross join public.size_bucket s
 where g.active and s.active
   and not exists (select 1 from public.stock_movement m
                    where m.grade_id = g.grade_id and m.size_id = s.size_id)
 order by g.sort_order, s.sort_order
 limit 1;

delete from public.stock_movement where reason like 'VERIFY-0043%';

insert into public.stock_movement
    (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct, ref_type, reason)
select current_date, grade_id, size_id, 'INTAKE', 100, 1000, 'verify', 'VERIFY-0043'
  from v43;

create or replace function pg_temp.v43_bal() returns numeric language sql as $$
    select coalesce((select p.balance_ct from public.v_stock_position p
                      join v43 v on v.grade_id = p.grade_id and v.size_id = p.size_id), 0);
$$;

create or replace function pg_temp.v43_rows() returns integer language sql as $$
    select count(*)::integer from public.stock_reservation
     where client_ref = (select ref from v43);
$$;


-- ── 1 · a line is entered ──────────────────────────────────────────────────
do $$
declare v record;
begin
    select * into v from v43;
    perform public.reserve_line(v.ref, 'line-1', v.grade_id, v.size_id, 10);
end $$;

insert into v43_out select 1, 'ENTRY  . entering a line takes it out at once: 100 - 10 = 90',
       case when pg_temp.v43_bal() = 90 then 'ok' else 'WRONG: ' || pg_temp.v43_bal()::text end;
insert into v43_out select 2, 'ENTRY  . and it is a RESERVATION, not a movement on the ledger',
       case when pg_temp.v43_rows() = 1
             and (select count(*) from public.stock_movement m
                   join v43 v on true
                  where m.grade_id = v.grade_id and m.size_id = v.size_id
                    and m.movement_type = 'SALE') = 0
            then 'ok' else 'A SALE WAS WRITTEN' end;


-- ── 2 · the weight is corrected ────────────────────────────────────────────
-- THE CHECK THIS FILE EXISTS FOR. Typing is corrected constantly; a second reservation beside the
-- first would hold 10 + 1 and the bucket would read 89.
do $$
declare v record;
begin
    select * into v from v43;
    perform public.reserve_line(v.ref, 'line-1', v.grade_id, v.size_id, 1);
end $$;

insert into v43_out select 3, 'EDIT   . correcting 10 to 1 leaves ONE hold: 100 - 1 = 99',
       case when pg_temp.v43_bal() = 99 then 'ok'
            when pg_temp.v43_bal() = 89 then 'DUPLICATE: both weights are held'
            else 'WRONG: ' || pg_temp.v43_bal()::text end;
insert into v43_out select 4, 'EDIT   . one row for that line, however often it changes',
       case when pg_temp.v43_rows() = 1 then 'ok' else pg_temp.v43_rows()::text || ' rows' end;


-- ── 3 · a second line, and one emptied back out ───────────────────────────
do $$
declare v record;
begin
    select * into v from v43;
    perform public.reserve_line(v.ref, 'line-2', v.grade_id, v.size_id, 4);
end $$;

insert into v43_out select 5, 'ENTRY  . a second line takes its own: 100 - 1 - 4 = 95',
       case when pg_temp.v43_bal() = 95 then 'ok' else 'WRONG: ' || pg_temp.v43_bal()::text end;

do $$
declare v record;
begin
    select * into v from v43;
    perform public.reserve_line(v.ref, 'line-2', v.grade_id, v.size_id, 0);
end $$;

insert into v43_out select 6, 'EMPTY  . emptying a line gives its carats straight back: 99',
       case when pg_temp.v43_bal() = 99 then 'ok' else 'WRONG: ' || pg_temp.v43_bal()::text end;
insert into v43_out select 7, 'EMPTY  . and leaves no row behind holding nothing',
       case when pg_temp.v43_rows() = 1 then 'ok' else pg_temp.v43_rows()::text || ' rows' end;


-- ── 4 · MOVE TO STOCK gives back what was taken, and no more ──────────────
do $$
declare v record; v_res jsonb;
begin
    select * into v from v43;
    perform public.reserve_line(v.ref, 'line-2', v.grade_id, v.size_id, 6);   -- 100 - 1 - 6 = 93
    v_res := public.release_entry(v.ref);

    insert into v43_out values
        (8, 'RETURN . Move to stock returns exactly what was held: 7 ct',
            case when (v_res->>'returned')::numeric = 7 then 'ok'
                 else 'RETURNED ' || (v_res->>'returned') end),
        (9, 'RETURN . and the bucket is whole again: 100',
            case when pg_temp.v43_bal() = 100 then 'ok'
                 else 'WRONG: ' || pg_temp.v43_bal()::text end);

    -- Pressing it twice cannot return anything a second time: there is nothing left to delete.
    v_res := public.release_entry(v.ref);
    insert into v43_out values
        (10, 'RETURN . pressing it again returns nothing, and cannot over-return',
             case when (v_res->>'returned')::numeric = 0 and pg_temp.v43_bal() = 100
                  then 'ok' else 'WRONG: ' || pg_temp.v43_bal()::text end);
end $$;


-- ── 5 · confirming converts the hold; it does not deduct again ────────────
do $$
declare v record; v_inv bigint;
begin
    select * into v from v43;

    perform public.reserve_line(v.ref, 'line-1', v.grade_id, v.size_id, 8);

    insert into public.sales_invoice
        (invoice_date, buyer_id, currency_id, status, doc_type, terms_days, client_ref)
    values (current_date,
            (select buyer_id from public.buyer where active order by buyer_id limit 1),
            (select currency_id from public.currency order by currency_id limit 1),
            'DRAFT', 'BILL', 30, v.ref)
    returning invoice_id into v_inv;

    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct, remark)
    values (v_inv, v.grade_id, v.size_id, 8, 6, 1000, 'VERIFY-0043');

    update v43 set invoice_id = v_inv;
end $$;

insert into v43_out select 11, 'HOLD   . the entry holds 8 before it is confirmed: 92',
       case when pg_temp.v43_bal() = 92 then 'ok' else 'WRONG: ' || pg_temp.v43_bal()::text end;

do $$
declare v record;
begin
    select * into v from v43;
    perform public.post_invoice(v.invoice_id, false);
end $$;

-- 100 - 8 = 92, the SAME figure as before confirming. The hold became the movements; if both were
-- counted the bucket would read 84.
insert into v43_out select 12, 'CONFIRM. confirming does NOT deduct again: still 92',
       case when pg_temp.v43_bal() = 92 then 'ok'
            when pg_temp.v43_bal() = 84 then 'DOUBLE DEDUCTION: held and moved'
            else 'WRONG: ' || pg_temp.v43_bal()::text end;
insert into v43_out select 13, 'CONFIRM. the hold is gone, the movements are there',
       case when pg_temp.v43_rows() = 0
             and (select count(*) from public.stock_movement m
                   join public.sales_line l on l.line_id = m.ref_id and m.ref_type = 'sales_line'
                  where l.invoice_id = (select invoice_id from v43)
                    and m.movement_type in ('SALE', 'REJECTION')) = 2
            then 'ok' else pg_temp.v43_rows()::text || ' hold(s) left' end;
insert into v43_out select 14, 'CONFIRM. and the bucket reconciles, as it always did',
       case when (select r.reconciles from public.v_reconciliation r
                   join v43 v on true
                   join public.grade g on g.grade_id = v.grade_id
                   join public.size_bucket s on s.size_id = v.size_id
                  where r.grade_code = g.code and r.size_code = s.code)
            then 'ok' else 'DOES NOT RECONCILE' end;


-- ── 6 · and what a reservation must never do ──────────────────────────────
do $$
declare v record; v_blocked boolean;
begin
    select * into v from v43;
    begin
        perform public.reserve_line(v.ref, 'line-9', v.grade_id, v.size_id, 99999);
        v_blocked := false;
    exception when others then v_blocked := true;
    end;
    insert into v43_out values
        (15, 'GUARD  . a line larger than the bucket is refused',
             case when v_blocked then 'ok' else 'WRONGLY ALLOWED' end),
        (16, 'GUARD  . and nothing was held by the attempt',
             case when pg_temp.v43_bal() = 92 then 'ok'
                  else 'WRONG: ' || pg_temp.v43_bal()::text end);
end $$;

insert into v43_out select 17, 'ALL    . no bucket anywhere is negative',
       case when (select count(*) from public.v_stock_position where balance_ct < 0) = 0
            then 'ok' else 'NEGATIVE BUCKETS PRESENT' end;
insert into v43_out select 18, 'ALL    . every bucket in the database reconciles',
       case when (select count(*) from public.v_reconciliation where not reconciles) = 0
            then 'ok'
            else (select string_agg(grade_code || ' x ' || size_code
                                    || ' (' || trim_scale(diff_ct) || ' ct)', ', ')
                    from public.v_reconciliation where not reconciles) end;


-- ── 7 · the answer, then remove everything this script created ────────────
select check_ as check, status from v43_out order by n;

delete from public.stock_reservation where client_ref in (select ref from v43);
delete from public.stock_movement
 where ref_type = 'sales_line'
   and ref_id in (select line_id from public.sales_line
                   where invoice_id in (select invoice_id from v43));
delete from public.sales_line   where invoice_id in (select invoice_id from v43);
delete from public.sales_invoice where invoice_id in (select invoice_id from v43);
delete from public.stock_movement where reason like 'VERIFY-0043%';

drop table if exists v43_out;
drop table if exists v43;
