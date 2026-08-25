-- ---------------------------------------------------------------------------
-- Are any two grades in the catalogue the same grade under two spellings?
--
-- READ ONLY. Nothing is inserted, updated or deleted.
--
-- Sizes are protected from this: sieve_key proves "6.5+" and "+6.5" are one
-- bucket, so add_size hands back the existing row instead of making a twin.
-- Grades have no equivalent. They match on the code and on the alias list, and
-- nothing else -- so "1BB" and "1 BB" are one grade to a person and two to the
-- app. This looks for pairs that have already drifted apart.
-- ---------------------------------------------------------------------------

-- ── 1 · same name once spacing, punctuation and case are ignored ──────────
-- The way a person reads two names as the same one. Anything listed here is a
-- pair somebody could reasonably expect to be a single grade.
with squashed as (
    select grade_id, code, coalesce(display_name, '') as display_name,
           coalesce(aliases, '') as aliases,
           upper(regexp_replace(code, '[^a-zA-Z0-9]', '', 'g')) as key
      from public.grade
)
select 'same name, different spelling' as finding,
       a.code as grade_a, a.grade_id::text as id_a,
       b.code as grade_b, b.grade_id::text as id_b,
       a.key  as reads_as
  from squashed a
  join squashed b on b.key = a.key and b.grade_id > a.grade_id
 order by a.key;


-- ── 2 · one grade's code is another grade's alias ─────────────────────────
-- Worse than the above, because the app WILL resolve that spelling -- to the
-- other grade. A sheet writing it lands somewhere the reader did not intend,
-- and nothing on screen says so.
with spellings as (
    select grade_id, code as owner, upper(btrim(code)) as spelling, 'code' as kind
      from public.grade
    union all
    select g.grade_id, g.code, upper(btrim(s)), 'alias'
      from public.grade g,
           lateral unnest(string_to_array(coalesce(g.aliases, ''), ';')) as s
     where btrim(s) <> ''
)
select 'a code that is also another grade''s alias' as finding,
       g.code                as grade,
       g.grade_id::text      as id,
       sp.owner              as claimed_by,
       sp.grade_id::text     as claimed_by_id,
       'a sheet writing "' || g.code || '" resolves to ' || sp.owner as consequence
  from public.grade g
  join spellings sp
    on sp.spelling = upper(btrim(g.code))
   and sp.grade_id <> g.grade_id
 order by g.code;


-- ── 3 · the same alias claimed by two grades ──────────────────────────────
-- Which of them wins is whichever the alias map happens to write last. That is
-- not a rule, it is an accident of ordering.
with spellings as (
    select g.grade_id, g.code, upper(btrim(s)) as spelling
      from public.grade g,
           lateral unnest(string_to_array(coalesce(g.aliases, ''), ';')) as s
     where btrim(s) <> ''
)
select 'an alias claimed by two grades' as finding,
       spelling                                as alias,
       string_agg(code, ' / ' order by code)   as claimed_by,
       count(*)::text                          as claimants
  from spellings
 group by spelling
having count(*) > 1
 order by spelling;


-- ── 4 · grades with no alias at all ───────────────────────────────────────
-- Not a fault. But a grade whose only spelling is its code will not match a
-- sheet that writes it any other way, and that is where new-grade surprises
-- come from.
select 'no alias, so only the exact code matches' as finding,
       g.code, g.grade_id::text as id,
       coalesce(g.display_name, '') as display_name,
       (select count(*)::text from public.stock_movement m where m.grade_id = g.grade_id) as movements
  from public.grade g
 where coalesce(btrim(g.aliases), '') = ''
   and g.active
 order by g.sort_order;
