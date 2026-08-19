-- ---------------------------------------------------------------------------
-- 0036 · A retired sieve can be brought back through the same popup that adds a new one.
--
-- WHY THIS SUPERSEDES 0035 RATHER THAN EDITING IT
--
-- 0035 is already applied to both projects. A migration that has run is a record of what
-- happened; editing it in place would leave the file disagreeing with every database that
-- has it, and the next person to read the folder would have no way to tell which version
-- their project actually holds. So this replaces the function instead.
--
-- WHAT CHANGES
--
-- 0035 made add_size REFUSE a retired sieve. That was right for the case it was written
-- for -- an import walking silently into a switched-off size -- but it is wrong as a
-- permanent rule: it made "retired" mean "gone forever", which nobody decided. The office
-- retires a sieve because it has stopped using it, and a sheet turning up next quarter with
-- that column on it is a normal event, not an error.
--
-- So the refusal moves from add_size to the place it belongs: the trigger. Stock cannot
-- LAND on a retired size (0035's trg_reject_retired_size, unchanged), but a person who is
-- looking at the popup and has read the heading off the paper can bring the sieve back.
-- The confirmation is the authority; the flag is not a lock.
--
-- WHAT DOES NOT CHANGE
--
-- Nothing here is named. There is no list of 14+, +18 or +23, and no size is special. A
-- retired sieve is any row with active = false, and the same rule restores any of them.
--
-- The twin rule is untouched and still the point of the whole file: the lookup is on
-- sieve_key, so answering yes to "6.5+" when "+6.5" exists RESTORES that row rather than
-- creating a second one. Restoring cannot create a duplicate, because it never inserts.
--
-- SAFE TO RUN REPEATEDLY. Touches no stock, no movement and no history.
-- ---------------------------------------------------------------------------

begin;

create or replace function public.add_size(p_code text, p_display_name text default null)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_code    text := btrim(coalesce(p_code, ''));
    v_id      bigint;
    v_active  boolean;
    v_new     boolean := false;
    v_restored boolean := false;
begin
    if v_code = '' then
        raise exception 'add_size needs a size code';
    end if;

    -- size_bucket.code is varchar(8). A longer "size" is a misread cell, not a sieve.
    if length(v_code) > 8 then
        raise exception 'add_size: "%" is too long to be a sieve size', v_code;
    end if;

    -- Existing row, matched on the normalised key rather than on the text, so a sheet
    -- writing "11+" finds the "+11" already there. This is what makes the whole thing
    -- duplicate-proof, and it applies to retired rows exactly as it does to live ones.
    select size_id, active into v_id, v_active
      from public.size_bucket
     where public.sieve_key(code) = public.sieve_key(v_code)
     limit 1;

    if v_id is null then
        insert into public.size_bucket (code, sort_order)
        values (v_code, (select coalesce(max(sort_order), 0) + 1 from public.size_bucket))
        returning size_id into v_id;
        v_new := true;

    elsif not v_active then
        -- Brought back, not recreated. The row keeps its size_id, so every movement,
        -- parcel and audit entry that ever pointed at this sieve still points at it and
        -- the history reads as one continuous thing rather than two sieves with a gap.
        update public.size_bucket
           set active = true, updated_at = now()
         where size_id = v_id;
        v_restored := true;
    end if;

    -- The pairings, or the size imports and can never be SOLD: 0018 guards sales_line with
    -- a trigger against grade_size. Same rule as 0018 and 0030 -- every grade except the
    -- '-2' exception, which is NO 1 and NO 1 BB only.
    --
    -- Written for a size this call CREATED, and also for one it RESTORED: 0035 deletes the
    -- pairings of a sieve it removes outright, and a restored row whose pairings went with
    -- it would come back unsellable. An already-live size is still left alone -- its
    -- pairings may have been changed on purpose, and add_size is not the repair tool.
    if v_new or v_restored then
        insert into public.grade_size (grade_id, size_id)
        select g.grade_id, v_id
          from public.grade g
         where public.sieve_key(v_code) <> public.sieve_key('-2')
            or g.code in ('NO 1', 'NO 1 BB')
        on conflict do nothing;
    end if;

    return jsonb_build_object('ok', true, 'size_id', v_id, 'created', v_new,
                              'restored', v_restored, 'code', v_code);
end;
$$;

revoke all     on function public.add_size(text, text) from public;
revoke all     on function public.add_size(text, text) from anon;
grant  execute on function public.add_size(text, text) to authenticated;

comment on function public.add_size(text, text) is
    'Adds a sieve size named on a stock or sale file, or restores one that was retired, with the grade pairings 0018''s rule gives it. Returns the EXISTING row when the code is the same bucket in another notation, so "6.5+" never becomes a twin of "+6.5".';

commit;


-- ---------------------------------------------------------------------------
-- Verification · read only. Every row must read 'ok'.
-- ---------------------------------------------------------------------------
select 'add_size exists' as check,
       case when exists (select 1 from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                          where n.nspname = 'public' and p.proname = 'add_size')
            then 'ok' else 'MISSING' end as status
union all
select 'the retired-size write barrier is still in place',
       case when (select count(*) from pg_trigger
                   where tgname = 'trg_reject_retired_size' and not tgisinternal) = 4
            then 'ok - 4 tables'
            else 'UNEXPECTED - ' || (select count(*)::text from pg_trigger
                                      where tgname = 'trg_reject_retired_size' and not tgisinternal) end
union all
select 'still one bucket per sieve',
       case when (select count(*) from (select public.sieve_key(code) k from public.size_bucket
                                         group by 1 having count(*) > 1) d) = 0
            then 'ok' else 'DUPLICATE SIEVE KEYS' end
union all
select 'sizes, live and retired',
       (select count(*) filter (where active)::text || ' live, '
             || count(*) filter (where not active)::text || ' retired'
          from public.size_bucket)
 order by 1;
