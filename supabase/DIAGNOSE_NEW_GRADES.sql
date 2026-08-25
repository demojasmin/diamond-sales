-- ---------------------------------------------------------------------------
-- Which grades did the sales import create, and why did they not match?
--
-- READ ONLY. Nothing is inserted, updated or deleted.
--
-- Three questions, in order:
--   1. Which grades are newest, and do they carry any sales?
--   2. Is each new one a near-duplicate of a grade that already existed?
--   3. Would the app's own alias rule have matched it?
-- ---------------------------------------------------------------------------

-- ── 1 · the newest grades, and what is riding on them ─────────────────────
select 'newest grades' as section,
       g.grade_id::text                                    as id,
       g.code,
       coalesce(g.display_name, '(none)')                  as display_name,
       coalesce(g.aliases, '(none)')                       as aliases,
       to_char(g.created_at, 'DD Mon HH24:MI')             as created,
       (select count(*)::text from public.sales_line   l where l.grade_id = g.grade_id) as sales_lines,
       (select count(*)::text from public.stock_movement m where m.grade_id = g.grade_id) as movements
  from public.grade g
 order by g.created_at desc, g.grade_id desc
 limit 6;


-- ── 2 · near-duplicates ───────────────────────────────────────────────────
-- Compared on the code with spaces, dots and dashes removed and case folded --
-- which is how a person reads two grade names as "the same one". The app does
-- NOT compare this way, and that difference is the whole question.
with squashed as (
    select grade_id, code, created_at,
           upper(regexp_replace(code, '[^a-zA-Z0-9]', '', 'g')) as key
      from public.grade
),
newest as (
    select * from squashed order by created_at desc, grade_id desc limit 2
)
select 'near duplicate' as section,
       n.code                                              as new_grade,
       o.code                                              as existing_grade,
       o.grade_id::text                                    as existing_id,
       case when n.key = o.key then 'SAME once spacing and case are ignored'
            else 'differs' end                             as verdict
  from newest n
  join squashed o on o.grade_id <> n.grade_id and o.key = n.key
 order by n.code;


-- ── 3 · would the app's own rule have matched it? ─────────────────────────
-- SaleFileImport.AliasMap keys on the CODE and on each entry in `aliases`,
-- case-insensitively, after trimming. It does not ignore internal spacing. So
-- a grade matches only if the sheet spells it exactly as the code or exactly as
-- one of the aliases, give or take case and surrounding whitespace.
with newest as (
    select grade_id, code from public.grade
     order by created_at desc, grade_id desc limit 2
),
spellings as (
    select g.grade_id, g.code as owner, upper(btrim(g.code)) as spelling
      from public.grade g
    union all
    select g.grade_id, g.code,
           upper(btrim(s))
      from public.grade g,
           lateral unnest(string_to_array(coalesce(g.aliases, ''), ';')) as s
     where btrim(s) <> ''
)
select 'why unmatched' as section,
       n.code                                              as new_grade,
       coalesce((select string_agg(distinct sp.owner, ', ')
                   from spellings sp
                  where sp.grade_id <> n.grade_id
                    and sp.spelling = upper(btrim(n.code))), '(nothing)')
                                                           as already_spelled_that_way,
       case when exists (select 1 from spellings sp
                          where sp.grade_id <> n.grade_id
                            and sp.spelling = upper(btrim(n.code)))
            then 'SHOULD have matched -- look closer, this is a bug'
            else 'no existing code or alias spells it this way, so a new grade was correct' end
                                                           as verdict
  from newest n
 order by n.code;


-- ── 4 · every grade, for eyeballing ───────────────────────────────────────
select 'catalogue' as section, g.grade_id::text as id, g.code,
       coalesce(g.display_name, '') as display_name,
       coalesce(g.aliases, '') as aliases,
       case when g.active then '' else 'inactive' end as state
  from public.grade g
 order by g.sort_order, g.grade_id;
