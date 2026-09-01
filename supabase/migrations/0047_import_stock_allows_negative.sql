-- ---------------------------------------------------------------------------
-- 0047  A replacing stock import may leave a bucket below zero.
--
-- 0038 refused an import whose projected position went negative, on the reasoning that a sheet
-- disagreeing with the ledger is a question for whoever holds the paper. The desk has decided
-- otherwise: negative stock is allowed, and an import must not be blocked by it.
--
-- SUPERSEDES 0038 RATHER THAN EDITING IT, for the reason 0036 gives about 0035: a migration that
-- has run is a record of what happened. Editing it would leave the file disagreeing with every
-- database that already has it, and the next person to read the folder could not tell which
-- version their project holds.
--
-- THE ONLY CHANGE IS THE REFUSAL. This function is 0038's, copied line for line, with the
-- negative-stock block removed and the two variables that block alone used (v_short, v_count)
-- taken out with it. Everything else is untouched and deliberately so:
--
--   * the empty-array refusal, which is what a silently failed parser returns
--   * the source check, and delete_imported_stock as the way to clear an import on purpose
--   * the parcels-and-movements pair written from one CTE, so a movement without its intake is
--     not expressible
--   * the replace path: same deletes, same batch, same return shape
--
-- assert_stock is NOT touched. Posting still obeys negative_stock_policy() as it always has; this
-- changes what an IMPORT refuses, and nothing about what a sale refuses.
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
