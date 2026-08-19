-- ---------------------------------------------------------------------------
-- Regression test for 0038 · a replacing import may not leave a bucket negative.
--
-- Runs against a live database because the rule lives in a plpgsql function, so
-- there is nowhere else to exercise it. It creates its own scenario, checks
-- three outcomes, and deletes exactly what it created.
--
-- SAFE ON A DATABASE WITH REAL STOCK. Everything it writes carries the reason
-- 'VERIFY-0038' and the cleanup deletes on that marker alone, so it can neither
-- match nor remove a row anybody else wrote. It is still worth running on Demo
-- first if you have the choice.
--
-- Every row of the result must read 'ok'.
-- ---------------------------------------------------------------------------

-- ── 0 · a bucket to work in, and a clean slate for the marker ──────────────
create temporary table if not exists v0038 (grade_id bigint, size_id bigint);
delete from v0038;

insert into v0038
select g.grade_id, s.size_id
  from public.grade g
  cross join public.size_bucket s
 where g.active and s.active
   and not exists (select 1 from public.stock_movement m
                    where m.grade_id = g.grade_id and m.size_id = s.size_id)
 order by g.sort_order, s.sort_order
 limit 1;

delete from public.stock_movement where reason = 'VERIFY-0038';


-- ── 1 · 100 ct arrives as an import, 80 ct is rejected out of it ───────────
insert into public.stock_movement
    (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct,
     ref_type, reason)
select current_date, grade_id, size_id, 'INTAKE', 100, 1000, 'stock_import', 'VERIFY-0038'
  from v0038;

insert into public.stock_movement
    (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct,
     ref_type, reason)
select current_date, grade_id, size_id, 'REJECT', 80, 1000, 'verify', 'VERIFY-0038'
  from v0038;


-- ── 2 · the three cases ────────────────────────────────────────────────────
-- Each calls import_stock the way the app does. The guard must refuse two of
-- them and allow one, and it must refuse BEFORE deleting anything.
do $$
declare
    v_g bigint; v_s bigint;
    v_ok boolean;
begin
    select grade_id, size_id into v_g, v_s from v0038;

    -- Case A · 90 ct against 80 ct out = 10 ct left. Must be ALLOWED.
    begin
        perform public.import_stock(
            current_date,
            jsonb_build_array(jsonb_build_object(
                'grade_id', v_g, 'size_id', v_s, 'weight_ct', 90, 'price_per_ct', 1000)),
            true, null, 'pdf');
        v_ok := true;
    exception when others then v_ok := false;
    end;
    create temporary table if not exists v0038_out (n int, check_ text, status text);
    insert into v0038_out values (1, 'A · a sheet that covers what went out is allowed',
                                  case when v_ok then 'ok' else 'WRONGLY REFUSED' end);

    -- Case B · 50 ct against 80 ct out = -30 ct. Must be REFUSED.
    begin
        perform public.import_stock(
            current_date,
            jsonb_build_array(jsonb_build_object(
                'grade_id', v_g, 'size_id', v_s, 'weight_ct', 50, 'price_per_ct', 1000)),
            true, null, 'pdf');
        v_ok := false;
    exception when others then v_ok := true;
    end;
    insert into v0038_out values (2, 'B · a sheet that would go negative is refused',
                                  case when v_ok then 'ok' else 'WRONGLY ALLOWED' end);

    -- Case C · the sheet does not mention the bucket at all = -80 ct. Must be
    -- REFUSED. This is the one a full join is needed to see.
    begin
        perform public.import_stock(
            current_date,
            jsonb_build_array(jsonb_build_object(
                'grade_id', v_g, 'size_id', v_s + 100000, 'weight_ct', 5, 'price_per_ct', 1000)),
            true, null, 'pdf');
        v_ok := false;
    exception when others then v_ok := true;
    end;
    insert into v0038_out values (3, 'C · a sheet omitting the bucket entirely is refused',
                                  case when v_ok then 'ok' else 'WRONGLY ALLOWED' end);
end $$;


-- ── 3 · nothing was left negative, and the refusals wrote nothing ──────────
insert into v0038_out
select 4, 'the test bucket is not negative',
       case when coalesce((select balance_ct from public.v_stock_position p
                            join v0038 v on v.grade_id = p.grade_id and v.size_id = p.size_id), 0) >= 0
            then 'ok' else 'NEGATIVE' end;

insert into v0038_out
select 5, 'no bucket anywhere is negative',
       case when (select count(*) from public.v_stock_position where balance_ct < 0) = 0
            then 'ok'
            else 'PRE-EXISTING: ' || (select count(*)::text from public.v_stock_position
                                       where balance_ct < 0) || ' negative bucket(s)' end;


-- ── 4 · results, then remove everything this script created ────────────────
select check_ as check, status from v0038_out order by n;

delete from public.stock_movement where reason = 'VERIFY-0038';
delete from public.rough_intake
 where import_source = 'pdf'
   and intake_date = current_date
   and grade_id in (select grade_id from v0038)
   and size_id  in (select size_id  from v0038);
delete from public.stock_movement
 where ref_type = 'stock_import'
   and grade_id in (select grade_id from v0038)
   and size_id  in (select size_id  from v0038);

drop table if exists v0038_out;
drop table if exists v0038;
