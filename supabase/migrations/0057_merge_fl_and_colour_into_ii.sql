-- ---------------------------------------------------------------------------
-- 0057. FL, NO II (#) and COL (color) become ONE grade, Roman II.
--
-- WHY. The office reads the three as one grade, "II". Until now they were shown together on the
-- Stock report and Stock page only (display grouping); every picker, filter, sale and stock check
-- still kept them apart. This makes it real.
--
-- HOW. NO II (the grade with the most history) is kept and every row that pointed at FL or COL is
-- moved onto it: ledger movements (both sides of a conversion), sales lines, intake parcels,
-- holds, rejection destinations and prices. Nothing is deleted except the two emptied catalogue
-- rows. Carats are not recomputed -- v_stock_position sums the moved movements, so II holds
-- exactly FL + # + color, bucket by bucket. The check at the end proves it and rolls back if not.
--
-- The old spellings become II's aliases, so a sheet or sales file that still prints "FL", "#" or
-- "color" imports into II rather than offering to create a grade.
--
-- KEPT AS IT WAS. Code stays 'NO II' (saved settings and the sheet rates refer to it); the app
-- prints it as "II". The FL and COL entries in app_config.stock_sheet_rates are left untouched
-- and simply no longer match a grade -- the app skips them. Audit history is kept; the moves
-- below add their own audit rows, which is the record that this was done.
--
-- One transaction. Refuses, changing nothing, if a grade is missing, or if a conversion ran
-- between two of the three (it would become a conversion from II into II).
-- ---------------------------------------------------------------------------
begin;

-- What was moved, so ROLLBACK_0057.sql can put every row back exactly. RLS on and no policy: the
-- API cannot read them, only the SQL editor.
create table if not exists public.merge_0057_grade      as select * from public.grade      with no data;
create table if not exists public.merge_0057_grade_size as select * from public.grade_size with no data;
create table if not exists public.merge_0057_moved
    (tbl text not null, row_id bigint not null, col text not null, old_grade_id bigint not null);
alter table public.merge_0057_grade      enable row level security;
alter table public.merge_0057_grade_size enable row level security;
alter table public.merge_0057_moved      enable row level security;

do $$
declare
    v_ii  bigint := (select grade_id from public.grade where code = 'NO II');
    v_fl  bigint := (select grade_id from public.grade where code = 'FL');
    v_col bigint := (select grade_id from public.grade where code = 'COL');
    v_old bigint[];
    v_before jsonb;
    v_after  jsonb;
begin
    if v_ii is null then raise exception '0057: grade NO II not found. Nothing was changed.'; end if;
    if v_fl is null and v_col is null then
        raise notice '0057: FL and COL are already gone. Nothing to do.';
        return;
    end if;
    v_old := array_remove(array[v_fl, v_col], null);

    if exists (select 1 from public.stock_movement
                where counterparty_grade_id is not null
                  and grade_id = any(v_old || v_ii) and counterparty_grade_id = any(v_old || v_ii)) then
        raise exception '0057: a conversion ran between FL, # and color. Nothing was changed.';
    end if;

    -- Carats per sieve across the three, before.
    select jsonb_object_agg(size_code, ct) into v_before from (
        select size_code, sum(ledger_ct) ct
          from public.v_stock_position where grade_code in ('NO II', 'FL', 'COL') group by size_code) t;

    insert into public.merge_0057_grade      select * from public.grade      where grade_id = any(v_old || v_ii);
    insert into public.merge_0057_grade_size select * from public.grade_size where grade_id = any(v_old || v_ii);
    insert into public.merge_0057_moved
    select 'stock_movement', movement_id, 'grade_id', grade_id from public.stock_movement where grade_id = any(v_old)
    union all select 'stock_movement', movement_id, 'counterparty_grade_id', counterparty_grade_id
                from public.stock_movement where counterparty_grade_id = any(v_old)
    union all select 'sales_line', line_id, 'grade_id', grade_id from public.sales_line where grade_id = any(v_old)
    union all select 'rough_intake', intake_id, 'grade_id', grade_id from public.rough_intake where grade_id = any(v_old)
    union all select 'stock_reservation', reservation_id, 'grade_id', grade_id
                from public.stock_reservation where grade_id = any(v_old)
    union all select 'rejection_disposition', disposition_id, 'to_grade_id', to_grade_id
                from public.rejection_disposition where to_grade_id = any(v_old)
    union all select 'price_list', price_id, 'grade_id', grade_id from public.price_list where grade_id = any(v_old);

    -- II is paired with every sieve any of the three was paired with (sales_line_grade_size_guard).
    insert into public.grade_size (grade_id, size_id)
    select distinct v_ii, size_id from public.grade_size where grade_id = any(v_old)
    on conflict do nothing;

    update public.stock_movement        set grade_id = v_ii              where grade_id = any(v_old);
    update public.stock_movement        set counterparty_grade_id = v_ii where counterparty_grade_id = any(v_old);
    update public.sales_line            set grade_id = v_ii              where grade_id = any(v_old);
    update public.rough_intake          set grade_id = v_ii              where grade_id = any(v_old);
    update public.stock_reservation     set grade_id = v_ii              where grade_id = any(v_old);
    update public.rejection_disposition set to_grade_id = v_ii           where to_grade_id = any(v_old);
    update public.price_list            set grade_id = v_ii              where grade_id = any(v_old);

    -- The old codes and their spellings resolve to II from now on. First in the sheet's order.
    update public.grade g
       set display_name = 'II',
           sort_order   = (select min(sort_order) from public.grade where grade_id = any(v_old || v_ii)),
           aliases      = (select string_agg(distinct a, ';')
                             from public.grade x,
                                  unnest(string_to_array(coalesce(x.aliases, ''), ';') || x.code) a
                            where x.grade_id = any(v_old || v_ii) and a <> '' and a <> 'NO II')
     where g.grade_id = v_ii;

    delete from public.grade where grade_id = any(v_old);      -- their grade_size rows cascade

    -- Carats per sieve, after: must equal before.
    select jsonb_object_agg(size_code, ct) into v_after from (
        select size_code, sum(ledger_ct) ct
          from public.v_stock_position where grade_code = 'NO II' group by size_code) t;

    if coalesce(v_before, '{}') <> coalesce(v_after, '{}') then
        raise exception '0057: carats changed (before %, after %). Nothing was changed.', v_before, v_after;
    end if;
end;
$$;

commit;

-- Verification: FL and COL gone, II holds what the three held.
select code, display_name, sort_order, aliases from public.grade where code in ('NO II', 'FL', 'COL');
select size_code, balance_ct, reserved_ct, ledger_ct from public.v_stock_position
 where grade_code = 'NO II' order by size_code;
