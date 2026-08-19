-- ---------------------------------------------------------------------------
-- 0038 · A replacing stock import may not leave a bucket below zero.
--
-- THE FAULT
--
-- import_stock(p_replace => true) deletes every stock_import movement and writes the new
-- sheet in its place. Sales, rejections and conversions are left alone -- correctly, they
-- are what happened -- but they were measured against the OLD baseline.
--
-- Replace a 300 ct sheet with a 107.76 ct one after 202.10 ct has gone out, and the
-- position lands at -94.34 ct. Which is exactly what Demo showed: three negative buckets
-- and a headline reading "Stock left that never arrived".
--
-- WHY negative_stock = block DID NOT CATCH IT
--
-- assert_stock runs when a movement is POSTED. It reads the position at that instant and
-- refuses to take a bucket below zero, and it did its job: every one of those rejections
-- was legal when it was recorded.
--
-- The import moved the floor afterwards. No movement was posted, so nothing called
-- assert_stock, and the balance went negative retroactively at a moment the policy had no
-- opportunity to see. A check that only fires on the way out cannot protect against the
-- baseline being lowered underneath it.
--
-- WHAT THIS DOES
--
-- Before deleting anything, it works out what every bucket WOULD hold once the old import
-- is gone and the new sheet is in: the new sheet's figure, plus every movement that is not
-- a stock import, signed exactly as v_stock_position signs it. Any bucket landing below
-- zero names itself and the import is refused whole -- nothing deleted, nothing written.
--
-- It refuses only under negative_stock = 'block', the same setting assert_stock obeys, so
-- one policy governs both doors rather than this one inventing a rule of its own.
--
-- WHAT IT DOES NOT DO
--
-- It writes no compensating adjustment and hides no balance. A shortfall means the sheet
-- and the ledger disagree, and that is a question for whoever holds the paper -- inventing
-- carats to make the arithmetic close would put a number in the client's stock that nobody
-- counted.
-- ---------------------------------------------------------------------------

begin;

create or replace function public.import_stock(
    p_as_at   date,
    p_rows    jsonb,
    p_replace boolean default true,
    p_batch   uuid    default null,
    p_source  text    default 'excel'
)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_old_ids bigint[];
    v_deleted integer := 0;
    v_written integer := 0;
    v_batch   uuid    := coalesce(p_batch, gen_random_uuid());
    v_short   text;
    v_count   integer;
begin
    if p_rows is null or jsonb_typeof(p_rows) <> 'array' then
        raise exception 'import_stock expects an array of rows';
    end if;

    -- Refuse to replace a real dataset with nothing. An empty array is what a
    -- parser returns when it silently failed, and "delete everything" is too
    -- destructive to be reachable by accident. Clearing the import on purpose
    -- is what delete_imported_stock() is for.
    if jsonb_array_length(p_rows) = 0 then
        raise exception 'import_stock was given no rows; use delete_imported_stock() to clear the import';
    end if;

    if p_source is not null and p_source not in ('excel', 'pdf') then
        raise exception 'import_stock does not know the source %', p_source;
    end if;

    if p_replace and public.negative_stock_policy() = 'block' then
        -- Everything that is NOT a stock import, signed the way v_stock_position signs it.
        -- Using the same rule matters more than it looks: weight_ct is stored positive for
        -- every movement type and the direction lives in movement_type, so a guard that
        -- summed the raw column would read a rejection as an arrival.
        with keeping as (
            select m.grade_id, m.size_id,
                   sum(case when m.movement_type in ('INTAKE', 'CONVERT_IN') then m.weight_ct
                            when m.movement_type = 'ADJUST'                  then m.weight_ct
                            else -m.weight_ct end) as other_ct
              from public.stock_movement m
             where m.ref_type is distinct from 'stock_import'
             group by m.grade_id, m.size_id
        ),
        arriving as (
            select (r->>'grade_id')::bigint as grade_id,
                   (r->>'size_id')::bigint  as size_id,
                   sum((r->>'weight_ct')::numeric) as new_ct
              from jsonb_array_elements(p_rows) as r
             group by 1, 2
        ),
        -- FULL join on purpose. A bucket the new sheet does not mention at all still holds
        -- whatever went out of it, and dropping to zero import while sales remain is the
        -- most likely way to go negative -- so the row must survive the join to be seen.
        resulting as (
            select coalesce(a.grade_id, k.grade_id)          as grade_id,
                   coalesce(a.size_id,  k.size_id)           as size_id,
                   coalesce(a.new_ct,   0)                   as new_ct,
                   coalesce(k.other_ct, 0)                   as other_ct,
                   coalesce(a.new_ct, 0) + coalesce(k.other_ct, 0) as balance
              from arriving a
              full join keeping k
                on k.grade_id = a.grade_id and k.size_id = a.size_id
        )
        select count(*),
               string_agg(
                   format('%s x %s: the sheet brings %s ct but %s ct has gone out against it, leaving %s ct',
                          coalesce(g.code, '?'), coalesce(s.code, '?'),
                          trim_scale(round(res.new_ct, 4)),
                          trim_scale(round(-res.other_ct, 4)),
                          trim_scale(round(res.balance, 4))),
                   E'\n  - ' order by g.code, s.code)
          into v_count, v_short
          from resulting res
          left join public.grade       g on g.grade_id = res.grade_id
          left join public.size_bucket s on s.size_id  = res.size_id
         where res.balance < -0.00005;

        if v_count > 0 then
            raise exception E'This sheet cannot replace the current stock: % bucket(s) would be left below zero.\n\n  - %\n\nNothing has been imported. Either this is not the sheet that matches these sales and rejections, or those movements were recorded against stock this sheet does not carry.',
                            v_count, v_short
                using errcode = 'check_violation';
        end if;
    end if;

    if p_replace then
        -- The parcels behind the movements, captured before the movements go.
        select coalesce(array_agg(distinct ref_id), '{}')
          into v_old_ids
          from public.stock_movement
         where ref_type = 'stock_import'
           and ref_id is not null;

        delete from public.stock_movement where ref_type = 'stock_import';
        get diagnostics v_deleted = row_count;

        if array_length(v_old_ids, 1) is not null then
            delete from public.rough_intake where intake_id = any(v_old_ids);
        end if;
    end if;

    -- Parcels and their movements in one statement each, both reading the same
    -- CTE, so a movement without its intake is not expressible here.
    with parcel as (
        insert into public.rough_intake
            (intake_date, grade_id, size_id, weight_ct, price_per_ct, created_by,
             import_batch, import_source)
        select p_as_at,
               (r->>'grade_id')::bigint,
               (r->>'size_id')::bigint,
               (r->>'weight_ct')::numeric,
               (r->>'price_per_ct')::numeric,
               auth.uid(),
               v_batch,
               p_source
          from jsonb_array_elements(p_rows) as r
        returning intake_id, grade_id, size_id, weight_ct, price_per_ct
    )
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select p_as_at, p.grade_id, p.size_id, 'INTAKE', p.weight_ct,
           p.price_per_ct, 'stock_import', p.intake_id, auth.uid()
      from parcel p;

    get diagnostics v_written = row_count;

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'written', v_written, 'batch', v_batch);
end;
$$;

revoke all on function public.import_stock(date, jsonb, boolean, uuid, text) from public;
grant execute on function public.import_stock(date, jsonb, boolean, uuid, text) to authenticated;

commit;


-- ---------------------------------------------------------------------------
-- Verification · read only. Every row must read 'ok'.
-- ---------------------------------------------------------------------------
select 'import_stock still exists' as check,
       case when exists (select 1 from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                          where n.nspname = 'public' and p.proname = 'import_stock')
            then 'ok' else 'MISSING' end as status
union all
select 'the guard is in it',
       case when (select prosrc from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                   where n.nspname = 'public' and p.proname = 'import_stock') like '%would be left below zero%'
            then 'ok' else 'MISSING' end
union all
select 'it signs movements the way v_stock_position does',
       case when (select prosrc from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                   where n.nspname = 'public' and p.proname = 'import_stock') like '%CONVERT_IN%'
            then 'ok' else 'NOT SIGNED' end
union all
select 'negative_stock policy',
       coalesce(public.negative_stock_policy(), '(unset)')
union all
select 'buckets negative right now',
       (select count(*)::text from public.v_stock_position where balance_ct < 0)
 order by 1;
