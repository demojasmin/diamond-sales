-- ---------------------------------------------------------------------------
-- Put the grades in the order the office reads them in.
--
-- ORDER ONLY. No code, no name, no alias, no pairing is touched -- this writes
-- one column, sort_order, and nothing else. Every grade list in the app is
-- ordered by it (Repo.GradesAsync sorts on sort_order; the Stock filter sorts on
-- SortOrder), so one column decides the order on every page at once.
--
-- THE LIST BELOW IS IN MARKS, MAPPED TO CODES. The screen shows the mark the
-- printed sheet uses -- "#", "2", "TOP co" -- while the database stores the code.
-- GradeNames.Marks in the app is what maps between them, and this is that mapping
-- written out:
--
--     #        NO II          2        NO 2           TOP co   TOP-COL
--     1BB      NO 1 BB        DX1      NO DX          color    COL
--     Ex1      EX 1           3..7     NO 3 .. NO 7   GH VS    GH
--
-- ANYTHING NOT LISTED KEEPS ITS PLACE, after the listed ones, in the order it is
-- in now. A grade the office did not mention is not a grade to hide at the
-- bottom in a random spot.
--
-- IT REFUSES ON A CODE IT CANNOT FIND rather than skipping it quietly. A typo
-- here would otherwise leave one grade sitting at its old number with no sign
-- anything went wrong.
-- ---------------------------------------------------------------------------

begin;

-- ── the guard · SET THIS FIRST ─────────────────────────────────────────────
--     Priya (handover) : Priya Sales
--     Demo             : Priya Gems
do $$
declare
    expected constant text := 'Priya Gems';   
    v text;
begin
    select value into v from public.app_config where key = 'company_name';
    if v is distinct from expected then
        raise exception
            'Refusing: this run is set for "%" and this database says "%". Nothing changed.',
            expected, coalesce(v, '(none)');
    end if;
end $$;


-- ── the order asked for, top to bottom ─────────────────────────────────────
create temporary table wanted (code text primary key, ord integer) on commit drop;

insert into wanted (code, ord) values
    ('PREMIUM',  1),   -- PREMIUM
    ('1MB',      2),   -- 1 MB   (the screen shows a space; the code has none)
    ('NO 1 BB',  3),   -- 1 BB
    ('FL',       4),   -- FL
    ('NO II',    5),   -- #
    ('EX 1',     6),   -- Ex1
    ('NO 2',     7),   -- 2
    ('NO DX',    8),   -- DX1
    ('NO 3',     9),   -- 3
    ('NO 4',    10),   -- 4
    ('NO 5',    11),   -- 5
    ('NO 6',    12),   -- 6
    ('NO 7',    13),   -- 7
    ('TOP-COL', 14),   -- TOP co
    ('COL',     15),   -- color
    ('OW',      16),   -- OW
    ('LC 1',    17),   -- LC 1
    ('LC 2',    18),   -- LC 2
    ('GH',      19),   -- GH VS
    ('LB 1',    20),   -- LB 1
    ('LB 2',    21),   -- LB 2
    ('MIX',     22);   -- MIX


-- ── nothing may be listed that is not there ────────────────────────────────
do $$
declare missing text;
begin
    select string_agg(w.code, ', ' order by w.ord) into missing
      from wanted w
     where not exists (select 1 from public.grade g where g.code = w.code);

    if missing is not null then
        raise exception
            'These codes are not in the catalogue: %. Nothing changed -- check the spelling against the preview query.',
            missing;
    end if;
end $$;


-- ── the listed grades take their places ────────────────────────────────────
update public.grade g
   set sort_order = w.ord
  from wanted w
 where g.code = w.code;

-- ── and everything else follows, keeping the order it already had ──────────
-- 100 upwards, so an unlisted grade can never collide with a listed one however
-- many are added to the list later.
with rest as (
    select g.grade_id,
           100 + row_number() over (order by g.sort_order, g.code) as ord
      from public.grade g
     where not exists (select 1 from wanted w where w.code = g.code)
)
update public.grade g
   set sort_order = r.ord
  from rest r
 where g.grade_id = r.grade_id;

commit;


-- ---------------------------------------------------------------------------
-- Verification. The first 22 rows must read exactly as the screen should.
-- ---------------------------------------------------------------------------
select sort_order, code, display_name, active
  from public.grade
 order by sort_order, code;

-- No two grades may share a number, or the order between them is whatever the
-- database feels like on the day. Must come back empty.
select sort_order, count(*) as grades_sharing_it, string_agg(code, ', ') as which
  from public.grade
 group by sort_order
having count(*) > 1
 order by sort_order;
