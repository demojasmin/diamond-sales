-- 0035 -- retire 14+, +18 and +23 from the active catalogue.
--
-- 14+ holds stock in both projects (281.99 ct in Priya, 28.00 ct in Demo), so it is switched
-- OFF, never deleted. Every movement, parcel and audit row keeps pointing at a row that still
-- exists, and the printed history keeps reconciling. BR-INV-1 -- a correction is a compensating
-- movement, never a deletion -- reads on the catalogue too: a sieve that held stock is part of
-- what happened, and what happened does not get edited.
--
-- +18 and +23 carry nothing in Priya. Rather than trust that reading for Demo as well, the
-- delete below re-runs the check per row at run time: unreferenced rows go, referenced rows are
-- retired instead. So this file is safe to run against either project, in either order, twice.

begin;

-- size_bucket is the ONLY catalogue table without this flag -- grade, buyer, broker and
-- profiles all carry `active`, and Repo already filters every one of them on it.
alter table public.size_bucket
    add column if not exists active boolean not null default true;

-- Matched through sieve_key, not on the literal text, so this finds the row whether the
-- project spells it "14+" or "+14" (0034 proved those are the same physical bucket).
update public.size_bucket
   set active = false, updated_at = now()
 where public.sieve_key(code) = public.sieve_key('14+')
   and active;

do $$
declare
    r      record;
    v_used boolean;
begin
    for r in
        select size_id, code from public.size_bucket
         where public.sieve_key(code) in (public.sieve_key('+18'), public.sieve_key('+23'))
    loop
        select exists (select 1 from public.stock_movement where size_id = r.size_id)
            or exists (select 1 from public.sales_line     where size_id = r.size_id)
            or exists (select 1 from public.rough_intake   where size_id = r.size_id)
            or exists (select 1 from public.price_list     where size_id = r.size_id)
          into v_used;

        if v_used then
            update public.size_bucket set active = false, updated_at = now() where size_id = r.size_id;
            raise notice '% holds transactional data -- RETIRED, not deleted', r.code;
        else
            delete from public.grade_size  where size_id = r.size_id;
            delete from public.size_bucket where size_id = r.size_id;
            raise notice '% deleted -- no transactional data', r.code;
        end if;
    end loop;
end $$;
-- add_size must not hand back a retired sieve.
--
-- Without this, a sheet headed "14+" walks straight through: sieve_key finds the switched-off
-- row, the function reports it as already existing, and the import posts 281.99 ct into a size
-- the catalogue says is gone -- silently, because from the app's side nothing failed. Refusing
-- here is what makes the flag mean anything at the only point that can still create stock.
create or replace function public.add_size(p_code text, p_display_name text default null)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_code   text := btrim(coalesce(p_code, ''));
    v_id     bigint;
    v_active boolean;
    v_new    boolean := false;
begin
    if v_code = '' then
        raise exception 'add_size needs a size code';
    end if;

    -- size_bucket.code is varchar(8). A longer "size" is a misread cell, not a sieve.
    if length(v_code) > 8 then
        raise exception 'add_size: "%" is too long to be a sieve size', v_code;
    end if;

    -- Existing row, matched on the normalised key rather than on the text, so a
    -- sheet writing "11+" finds the "+11" already there.
    select size_id, active into v_id, v_active
      from public.size_bucket
     where public.sieve_key(code) = public.sieve_key(v_code)
     limit 1;

    if v_id is not null and not v_active then
        raise exception 'The sieve size "%" has been retired and cannot take new stock. '
                        'Ask the owner to restore it in Master data if this sheet is still in use.',
                        v_code;
    end if;

    if v_id is null then
        insert into public.size_bucket (code, sort_order)
        values (v_code, (select coalesce(max(sort_order), 0) + 1 from public.size_bucket))
        returning size_id into v_id;
        v_new := true;
    end if;

    -- The pairings, or the size imports and can never be SOLD: 0018 guards
    -- sales_line with a trigger against grade_size. Same rule as 0018 and 0030 --
    -- every grade except the '-2' exception, which is NO 1 and NO 1 BB only.
    --
    -- ONLY FOR A SIZE THIS CALL CREATED. Asking for a size that already exists must
    -- write nothing at all: an existing sieve may have had a pairing removed on
    -- purpose, and "add_size" quietly restoring it would be this function editing a
    -- catalogue it was only asked to read.
    if v_new then
        insert into public.grade_size (grade_id, size_id)
        select g.grade_id, v_id
          from public.grade g
         where public.sieve_key(v_code) <> public.sieve_key('-2')
            or g.code in ('NO 1', 'NO 1 BB')
        on conflict do nothing;
    end if;

    return jsonb_build_object('ok', true, 'size_id', v_id,
                              'created', v_new, 'code', v_code);
end;
$$;

-- The actual boundary.
--
-- Filtering the desktop pickers stops the desk from choosing a retired sieve. It does not stop
-- the Android app, a queued outbox payload written before the size was retired, a direct
-- PostgREST call, or anyone with the anon key and five minutes. "Not writable anywhere" is a
-- claim only the database can make, so it is made here.
--
-- Reads are untouched: every existing movement, parcel, line and price stays exactly as it is,
-- selectable, reportable and auditable. This refuses only NEW entries.
create or replace function public.reject_retired_size()
returns trigger
language plpgsql
as $$
declare
    v_code text;
begin
    -- An UPDATE that does not move the row to a different sieve is left alone: correcting a
    -- weight or a rate on a historical line must keep working, and so must anything that only
    -- touches updated_at. Only arriving AT a retired size is refused.
    if tg_op = 'UPDATE' and new.size_id is not distinct from old.size_id then
        return new;
    end if;

    select code into v_code
      from public.size_bucket
     where size_id = new.size_id
       and not active;

    if found then
        raise exception 'The sieve size "%" has been retired and takes no new entries. '
                        'Its existing stock can still be read and reported.', v_code
            using errcode = 'check_violation';
    end if;

    return new;
end;
$$;

do $$
declare t text;
begin
    foreach t in array array['stock_movement', 'sales_line', 'rough_intake', 'price_list']
    loop
        execute format('drop trigger if exists trg_reject_retired_size on public.%I', t);
        execute format(
            'create trigger trg_reject_retired_size
               before insert or update of size_id on public.%I
               for each row execute function public.reject_retired_size()', t);
    end loop;
end $$;

commit;
