-- ---------------------------------------------------------------------------
-- 0027 · Stock imports arrive in batches, and an import can now APPEND.
--
-- WHY THIS EXISTS
--
-- Until now "import stock" meant exactly one thing: replace. 0018 made that
-- atomic, and it is still the right default -- a stock sheet is a full count,
-- and merging one into another silently doubles every bucket that appears on
-- both.
--
-- But the client also loads a printed sheet per parcel lot, and two of those
-- are not one count restated, they are two counts that both stand. So the
-- import now asks which it is, and the answer travels as a boolean rather than
-- as two functions that would drift apart.
--
-- WHAT IS NOT CHANGING
--
-- replace_imported_stock(date, jsonb) keeps its signature and its behaviour to
-- the byte. It is now a two-line wrapper over import_stock(...). Anything
-- already calling it -- an older desktop build, the offline outbox replaying a
-- queued Excel import written before today -- keeps working unchanged. That is
-- the whole reason it was not simply given two more arguments: adding
-- defaulted parameters to a function creates a SECOND overload rather than
-- replacing the first, and PostgREST then refuses both as ambiguous (PGRST203).
--
-- The batch is stamped on rough_intake, not on stock_movement. The movement
-- already points at its parcel through ref_id, and one id in one place cannot
-- disagree with itself. ref_type stays 'stock_import' for both kinds, so
-- v_stock_position, the reconciliation, the breakdown card and the fingerprint
-- the offline queue guards on all carry on reading exactly what they read
-- before.
-- ---------------------------------------------------------------------------

alter table public.rough_intake
    add column if not exists import_batch  uuid,
    add column if not exists import_source text;

comment on column public.rough_intake.import_batch is
    'Groups the parcels one import wrote, so an appended sheet can be told from the one before it. Null for parcels typed in by hand.';
comment on column public.rough_intake.import_source is
    'How the parcel arrived: excel, pdf, or null for a hand-entered intake.';

-- Partial: hand-entered intakes are the majority and all carry null here.
create index if not exists rough_intake_import_batch_idx
    on public.rough_intake (import_batch)
    where import_batch is not null;


-- ---------------------------------------------------------------------------
-- The import itself. SECURITY DEFINER for the reason 0016 and 0018 give:
-- `authenticated` has no DELETE on stock_movement and must never be granted
-- it. This function can still only reach rows it can prove are its own --
-- ref_type = 'stock_import'. A hand intake, a sale and a conversion are all
-- unreachable from here, and search_path is pinned so definer rights cannot be
-- redirected at another schema.
-- ---------------------------------------------------------------------------
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


-- ---------------------------------------------------------------------------
-- The old name, unchanged in behaviour. Kept so nothing that already calls it
-- has to be redeployed in step with the database.
-- ---------------------------------------------------------------------------
create or replace function public.replace_imported_stock(
    p_as_at date,
    p_rows  jsonb
)
returns jsonb
language sql
security definer
set search_path = public
as $$
    select public.import_stock(p_as_at, p_rows, true, null, 'excel');
$$;

revoke all on function public.replace_imported_stock(date, jsonb) from public;
grant execute on function public.replace_imported_stock(date, jsonb) to authenticated;


-- ---------------------------------------------------------------------------
-- What has been imported, one row per batch. Read by the import dialog so the
-- user choosing "keep" or "replace" can see what they are keeping or replacing
-- rather than being asked in the abstract.
--
-- Parcels written before this migration have no batch. They are reported as
-- one row with a null id rather than hidden -- carats that exist and are not
-- listed is the failure mode worth avoiding here.
-- ---------------------------------------------------------------------------
create or replace view public.v_stock_import_batch as
-- Ordered by last_intake_id rather than a timestamp: intake_id is monotonic and
-- certain to exist, and "which batch landed last" is the only ordering the
-- dialog needs.
select i.import_batch                        as batch_id,
       coalesce(i.import_source, 'excel')    as source,
       min(i.intake_date)                    as as_at,
       max(i.intake_id)                      as last_intake_id,
       count(*)::integer                     as parcels,
       round(sum(i.weight_ct), 4)            as carats,
       round(sum(i.weight_ct * i.price_per_ct), 2) as value
  from public.rough_intake i
 where exists (select 1
                 from public.stock_movement m
                where m.ref_type = 'stock_import'
                  and m.ref_id  = i.intake_id)
 group by i.import_batch, coalesce(i.import_source, 'excel');

grant select on public.v_stock_import_batch to authenticated;
