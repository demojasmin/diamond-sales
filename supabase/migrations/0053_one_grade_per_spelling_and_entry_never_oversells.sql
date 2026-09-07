-- ---------------------------------------------------------------------------
-- 0053. One grade per spelling, and a sales entry that never oversells a bucket.
--
-- TWO FAULTS, ONE REPORT. "Stock is not deducting properly" turned out to be a
-- pair of them, and they compound: the carats sit under one grade row while the
-- sale is entered against another, so the bucket the sale names is empty -- and
-- nothing then refused the sale for being short.
--
--
-- 1 . THE SAME GRADE UNDER TWO SPELLINGS
--
-- The app SHOWS a mark and the database STORES a code: GradeNames.Marks prints
-- "NO DX" as "DX1", "GH" as "GH VS", "NO II" as "#". The importers do not use
-- that map -- they resolve a sheet's grade names through grade.aliases -- and
-- the marks were never put there. So a sheet printing "DX1" (which is what the
-- client's own printed stock sheet prints, docs/14 and the PDF spec both say
-- so) matched nothing, the importer offered to create the missing grade, and
-- somebody said yes. The position then splits across two rows that are the same
-- goods, and every picker shows the mark twice with no way to tell them apart.
--
-- Worse, add_grade's own duplicate check was CASE AND PUNCTUATION SENSITIVE:
-- `code = v_code` and a LIKE against the alias list. "Dx" matched neither "DX"
-- nor the alias "DX", so answering yes to it created a third row.
--
-- Fixed at both ends of the same rule:
--
--   grade_key()   normalises a grade name the way a person reads one -- case,
--                 spacing and punctuation ignored. The exact counterpart of
--                 sieve_key (0037), which is what has kept sieves from twinning
--                 since 0034, and the same expression DIAGNOSE_GRADE_DUPLICATES
--                 already uses to FIND grades that have drifted apart.
--
--   the marks     go into grade.aliases, so "DX1", "GH VS", "#", "TOP co" and
--                 "color" resolve to the grade the screen prints them for. Same
--                 shape as 0013 and 0018, and it closes the whole class rather
--                 than the one name that was reported: StockFileImport.GradeKey
--                 mirrors grade_key so the app and the database agree about what
--                 counts as a duplicate, exactly as SizeKey mirrors sieve_key.
--
-- Between them: Dx, DX, dx, NO-DX, NODX, Dx1, DX1, dx1 and dX1 are ONE grade,
-- and add_grade hands back NO DX for every one of them instead of creating a
-- row. SUPERSEDES 0030's add_grade; 0030 stays on disk and stays applied.
--
--
-- 2 . A SALES ENTRY COULD RESERVE CARATS THAT WERE NOT THERE
--
-- reserve_line (0043) only refused when negative_stock_policy() reads 'block'.
-- Under 'warn' -- the default an unset key gives, and what DiamondApi seeds --
-- it reserved whatever was typed and drove balance_ct straight past zero, with
-- nothing said. A bucket holding 0 ct accepted a 3.43 ct line silently.
--
-- It now refuses whenever the BUCKET THE LINE NAMES cannot cover the line, and
-- says both figures: "Insufficient stock for NO II x 1/6: required 3.43 ct,
-- available 0 ct."
--
-- WHY THE POLICY NO LONGER GATES THIS. A reservation is a claim on what is on
-- the shelf, and there is nothing beyond the shelf to claim -- so there is no
-- honest reading of 'warn' or 'allow' that lets one overdraw. The policy still
-- governs everything it governed before, untouched: post_invoice's block /
-- warn+override / allow (0014), the corrections in 0040 and 0042, assert_stock
-- for rejections and adjustments, and the import, which since 0047 may take a
-- position negative and since 0051 caps each imported line at what its bucket
-- holds. Refusing the HOLD costs the desk nothing it had: the line stays on
-- screen, and confirming still goes through post_invoice's own policy.
--
-- PER BUCKET, and it always was: the check, the hold and the movements are all
-- keyed (grade_id, size_id), so carats are never taken from a bucket other than
-- the one the line names however much total stock exists elsewhere.
--
-- SUPERSEDES 0043's reserve_line. Nothing else in 0043 changes: one row per
-- (client_ref, line_key), release_line, release_entry and the release-on-post
-- trigger are all as they were.
--
-- Writes no movement, moves no stock, deletes nothing. Safe to run repeatedly.
-- ---------------------------------------------------------------------------

begin;

-- ---------------------------------------------------------------------------
-- A grade name reduced to what makes it that grade.
--
-- Case, spacing and punctuation are how a name is WRITTEN; they are not what it
-- names. "NO DX", "no-dx", "NoDx" and "N O D X" are one grade to anybody
-- reading them and must be one grade here.
--
-- NULL when nothing alphanumeric survives, as sieve_key returns null for a
-- label that is not a size. "#" is a real mark and a legitimate alias, and it
-- keys to nothing at all -- so the key must not be '' for it, or every
-- punctuation-only mark would collide with every other.
--
-- DELIBERATELY NOT CLEVER. It does not strip a trailing digit: "NO 1" and
-- "NO 2" differ by exactly that, and the printed sheets name grades "2", "3"
-- and "7" outright. DX1 resolving to NO DX is an ALIAS -- a fact about this
-- catalogue -- not a rule about names.
-- ---------------------------------------------------------------------------
create or replace function public.grade_key(p_code text)
returns text
language sql
immutable
as $$
    select nullif(upper(regexp_replace(coalesce(p_code, ''), '[^a-zA-Z0-9]', '', 'g')), '');
$$;

comment on function public.grade_key(text) is
    '0053. Normalises a grade name so case, spacing and punctuation cannot make one grade into two: "NO DX", "no-dx" and "NoDx" share a key. NULL when nothing alphanumeric remains, so a punctuation-only mark such as "#" keys to nothing rather than colliding. Mirrors StockFileImport.GradeKey, and is to grades what sieve_key is to sieves.';


-- ---------------------------------------------------------------------------
-- The marks the app PRINTS, as aliases the importers can resolve.
--
-- Exactly GradeNames.Marks in DiamondDesktop/Data/Models.cs, and that is the
-- point: what the screen calls a grade is what somebody types into a sheet.
-- Additive, and the ';' sentinels are 0013's idiom -- without them '%;2;%'
-- would also match a hypothetical ";25;".
--
-- Most of these are already there ("1BB", "EX1", "2", "3".."7", "color"); the
-- ones that were missing are "#", "DX1", "TOP co" and "GH VS", and DX1 is the
-- one that was reported. Listing all thirteen is what stops the next one going
-- missing.
-- ---------------------------------------------------------------------------
update public.grade g
set    aliases = case
           when coalesce(g.aliases, '') = '' then t.alias
           else g.aliases || ';' || t.alias
       end
from (values
        ('NO II',   '#'),      ('NO 1 BB', '1BB'),   ('EX 1',  'EX1'),
        ('NO 2',    '2'),      ('NO DX',   'DX1'),   ('NO 3',  '3'),
        ('NO 4',    '4'),      ('NO 5',    '5'),     ('NO 6',  '6'),
        ('NO 7',    '7'),      ('TOP-COL', 'TOP co'), ('COL',  'color'),
        ('GH',      'GH VS')
     ) as t(code, alias)
where  g.code = t.code
  and  not (';' || coalesce(g.aliases, '') || ';') like ('%;' || t.alias || ';%');


-- ---------------------------------------------------------------------------
-- add_grade, now unable to twin a grade over how its name is spelled.
--
-- 0030's body verbatim except for the lookup, which gained a second pass. The
-- first is 0030's own -- exact code, exact alias -- so a database that resolved
-- a name one way yesterday resolves it the same way today. Only a name that
-- found NOTHING reaches the key pass, and it is the oldest matching grade that
-- wins, so the answer cannot depend on row order.
-- ---------------------------------------------------------------------------
create or replace function public.add_grade(p_code text, p_display_name text default null)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_code text := btrim(coalesce(p_code, ''));
    v_id   bigint;
    v_new  boolean := false;
begin
    if v_code = '' then
        raise exception 'add_grade needs a grade code';
    end if;

    -- A guard, not a validation rule. Grade codes are trade shorthand -- "GH",
    -- "TOP-COL", "-2 MB" -- so almost anything short is legitimate. What this
    -- catches is a whole table row arriving as a name, which is exactly what a
    -- misread PDF produces ("1BB 6.01 11.80 3.05 1 BB").
    if length(v_code) > 20 then
        raise exception 'add_grade: "%" is too long to be a grade code', v_code;
    end if;

    -- Existing code OR existing alias, 0030's pass unchanged. The alias arm is
    -- why adding "DX" does not create a second grade beside NO DX.
    select grade_id into v_id
      from public.grade
     where code = v_code
        or (';' || coalesce(aliases, '') || ';') like ('%;' || v_code || ';%')
     limit 1;

    -- 0053. And the same name written differently. "Dx" is neither the code
    -- "DX" nor a LIKE match for the alias "DX" -- LIKE is case sensitive -- so
    -- until now it created a row, and the stock split across the two.
    if v_id is null then
        select grade_id into v_id
          from public.grade g
         where public.grade_key(g.code) = public.grade_key(v_code)
            or exists (select 1
                         from unnest(string_to_array(coalesce(g.aliases, ''), ';')) as a
                        where public.grade_key(a) = public.grade_key(v_code))
         order by g.grade_id
         limit 1;
    end if;

    if v_id is null then
        insert into public.grade (code, display_name, sort_order, active)
        values (v_code,
                coalesce(nullif(btrim(p_display_name), ''), v_code),
                (select coalesce(max(sort_order), 0) + 1 from public.grade),
                true)
        returning grade_id into v_id;
        v_new := true;
    end if;

    -- The size pairings, or the grade imports and can never be SOLD. 0018 puts a
    -- trigger on sales_line against grade_size; a grade added without pairings
    -- passes the stock import -- that trigger is on sales_line, not
    -- stock_movement -- and then fails at the first invoice with "Grade MIX does
    -- not use size 14+", which reads as a bug rather than as a missing row.
    --
    -- Same rule as 0018 and 0029: every size except '-2', which is NO 1 and
    -- NO 1 BB only.
    insert into public.grade_size (grade_id, size_id)
    select v_id, s.size_id
      from public.size_bucket s
     where s.code <> '-2'
    on conflict do nothing;

    return jsonb_build_object('ok', true, 'grade_id', v_id,
                              'created', v_new, 'code', v_code);
end;
$$;

comment on function public.add_grade(text, text) is
    '0053. Adds a grade named on a stock sheet that the catalogue does not have, with the size pairings 0018''s rule gives it. Idempotent, and returns the EXISTING grade when the name is already a code, already an alias, or either of those written with different case, spacing or punctuation (grade_key). Supersedes 0030, whose match was case sensitive and so created a second row for "Dx".';


-- ---------------------------------------------------------------------------
-- reserve_line. The bucket the line names, and never past what it holds.
--
-- 0043's body except for the check. What changed:
--
--   * it refuses on a shortfall WHATEVER the negative-stock policy says. See
--     the header: a reservation cannot claim carats that are not on the shelf,
--     and posting's block / warn+override / allow is untouched.
--   * the refusal names the bucket and BOTH figures. "Only 6 ct available in
--     that bucket" left the desk to work out what the line had asked for.
--
-- The room is read from v_stock_position.balance_ct for THIS grade_id and
-- size_id and no other, which is what makes a bucket's shortfall unfixable by
-- stock sitting in a different grade or a different sieve.
--
-- Measured with this line's OWN existing hold added back, or correcting 10.00
-- down to 9.00 would be judged against a balance the old 10.00 is still being
-- subtracted from.
-- ---------------------------------------------------------------------------
create or replace function public.reserve_line(
    p_client_ref uuid,
    p_line_key   text,
    p_grade_id   bigint,
    p_size_id    bigint,
    p_weight_ct  numeric
)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_available numeric;
    v_grade     text;
    v_size      text;
begin
    if p_client_ref is null or coalesce(btrim(p_line_key), '') = '' then
        raise exception 'reserve_line needs an entry and a line';
    end if;

    if p_weight_ct is null or p_weight_ct <= 0 then
        -- Nothing to hold. A line emptied back out releases instead of reserving zero, so no row
        -- is left behind claiming nothing.
        delete from public.stock_reservation
         where client_ref = p_client_ref and line_key = p_line_key;
        return jsonb_build_object('ok', true, 'reserved', 0);
    end if;

    select coalesce(sp.balance_ct, 0)
           + coalesce((select r.weight_ct from public.stock_reservation r
                        where r.client_ref = p_client_ref and r.line_key = p_line_key), 0),
           sp.grade_code, sp.size_code
      into v_available, v_grade, v_size
      from public.v_stock_position sp
     where sp.grade_id = p_grade_id and sp.size_id = p_size_id;

    -- v_stock_position is grade CROSS JOIN size_bucket, so every real pair has a row and this is
    -- only reachable with an id that is not in the catalogue at all. Said plainly rather than
    -- reported as a shortfall of an unnamed bucket.
    if not found then
        raise exception 'reserve_line: grade % x size % is not a bucket this catalogue holds',
            p_grade_id, p_size_id using errcode = 'no_data_found';
    end if;

    if coalesce(v_available, 0) < p_weight_ct then
        -- Floored at zero. A bucket already overdrawn by earlier trading has nothing to give, and
        -- "available -12 ct" is a ledger fact for the Stock page rather than an answer to "how
        -- much can I sell". trim_scale so 3.4300 reads as 3.43.
        raise exception 'Insufficient stock for % x %: required % ct, available % ct.',
            coalesce(v_grade, '?'), coalesce(v_size, '?'),
            trim_scale(round(p_weight_ct, 4)),
            trim_scale(round(greatest(coalesce(v_available, 0), 0), 4))
            using errcode = 'check_violation';
    end if;

    insert into public.stock_reservation
        (client_ref, line_key, grade_id, size_id, weight_ct, created_by)
    values (p_client_ref, p_line_key, p_grade_id, p_size_id, p_weight_ct, auth.uid())
    on conflict (client_ref, line_key) do update
        set grade_id  = excluded.grade_id,
            size_id   = excluded.size_id,
            weight_ct = excluded.weight_ct;

    return jsonb_build_object('ok', true, 'reserved', p_weight_ct);
end;
$$;

comment on function public.reserve_line(uuid, text, bigint, bigint, numeric) is
    '0053. Holds what one sales-entry line says against its own grade x size bucket, upserting on (client_ref, line_key) so an edit corrects the hold instead of adding another. Refuses when that bucket cannot cover the line -- whatever negative_stock_policy() says, because a reservation cannot claim carats the shelf does not have -- and names the required and available carats. Supersedes 0043, which refused only under the block policy and so let the position go negative silently under the default.';

commit;


-- ---------------------------------------------------------------------------
-- Verification. Every row must read 'ok'.
--
-- Two of these CALL add_grade, because add_grade is what is being tested and
-- proving it through anything else proves something else. That is safe: every
-- name they pass already resolves on a seeded catalogue, so nothing is created
-- -- and the row that says "and none of them created a row" is what reports it
-- if that ever stops being true. The rest read the catalogue and nothing more.
-- ---------------------------------------------------------------------------
select 'case and punctuation are ignored' as check,
       case when public.grade_key('Dx')    = public.grade_key('DX')
             and public.grade_key('no dx') = public.grade_key('NO-DX')
             and public.grade_key('NoDx')  = public.grade_key('NODX')
             and public.grade_key('Dx1')   = public.grade_key('DX1')
             and public.grade_key('dX1')   = public.grade_key('dx1')
            then 'ok' else 'BROKEN' end as status

union all
select 'a punctuation-only mark keys to nothing',
       case when public.grade_key('#') is null and public.grade_key('  ') is null
            then 'ok' else 'BROKEN' end

union all
select 'a trailing digit still tells two grades apart',
       case when public.grade_key('NO 1') <> public.grade_key('NO 2')
             and public.grade_key('LC 1') <> public.grade_key('LC 2')
            then 'ok' else 'GRADES COLLAPSED' end

union all
select 'the DX family is one grade',
       case when (select count(distinct (public.add_grade(s.spelling) ->> 'grade_id'))
                    from unnest(array['DX', 'Dx', 'dx', 'DX1', 'Dx1', 'dx1', 'dX1',
                                      'NO DX', 'no-dx', 'NODX']) as s(spelling)) = 1
            then 'ok' else 'STILL SPLIT' end

union all
select 'and none of them created a row',
       case when (select count(*)
                    from unnest(array['DX', 'Dx', 'dx', 'DX1', 'Dx1', 'dx1', 'dX1',
                                      'NO DX', 'no-dx', 'NODX']) as s(spelling)
                   where (public.add_grade(s.spelling) ->> 'created') = 'true') = 0
            then 'ok' else 'CREATED A DUPLICATE' end

union all
-- Every mark GradeNames.Marks prints is an alias of the grade it prints it for. Joined on the
-- grade rather than looked up, so a catalogue that simply lacks one of these grades reports
-- nothing about it instead of failing over a row it does not have.
select 'the printed marks are all aliases',
       case when (select count(*)
                    from (values ('NO II', '#'),      ('NO 1 BB', '1BB'), ('EX 1', 'EX1'),
                                 ('NO 2', '2'),       ('NO DX', 'DX1'),   ('NO 3', '3'),
                                 ('NO 4', '4'),       ('NO 5', '5'),      ('NO 6', '6'),
                                 ('NO 7', '7'),       ('TOP-COL', 'TOP co'),
                                 ('COL', 'color'),    ('GH', 'GH VS')) as m(code, mark)
                    join public.grade g on g.code = m.code
                   where not (';' || coalesce(g.aliases, '') || ';') like ('%;' || m.mark || ';%')) = 0
            then 'ok' else 'A MARK IS MISSING FROM grade.aliases' end

union all
select 'no two grades share a key',
       case when (select count(*) from (select public.grade_key(code) k from public.grade
                                         group by 1 having count(*) > 1) d) = 0
            then 'ok' else 'DUPLICATE GRADES EXIST - see DIAGNOSE_GRADE_DUPLICATES.sql' end

union all
select 'the entry hold refuses a shortfall',
       case when pg_get_functiondef('public.reserve_line'::regproc) like '%Insufficient stock for%'
             and pg_get_functiondef('public.reserve_line'::regproc) not like '%negative_stock_policy%'
            then 'ok' else 'NOT APPLIED' end

union all
select 'and posting still obeys the policy',
       case when pg_get_functiondef('public.post_invoice'::regproc) like '%negative_stock_policy%'
            then 'ok' else 'THE POLICY WAS LOST' end
 order by 1;


-- ---------------------------------------------------------------------------
-- AND THE ONE THING THIS DOES NOT DO. If a duplicate grade row was ALREADY
-- created -- the check above says so -- this migration does not fold it away.
-- It cannot: the two rows may both hold movements, lines and prices, and which
-- one keeps the carats is the office's decision, not a migration's. Run
-- DIAGNOSE_GRADE_DUPLICATES.sql to see the pairs, and merge them the way
-- supabase/merge_stray_gh_vs_grade.sql does, one pair at a time, with its
-- company-name guard set. Nothing new can appear beside them from here.
-- ---------------------------------------------------------------------------
