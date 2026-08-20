-- ---------------------------------------------------------------------------
-- 0039 · "Unknown Grade", so a row the sheet printed no grade against is
--         imported rather than refused.
--
-- WHY THIS EXISTS
--
-- The PDF reader used to stop the whole file when a printed row carried carats
-- but no grade name, on the reasoning that placing them under the row above or
-- the row below would be a guess. The reasoning is sound; the conclusion was
-- not. An import that refuses loses exactly as many carats as one that drops a
-- line -- and it loses the other twenty rows with them. A stock count sat
-- unimportable over one blank cell in one column.
--
-- So the reader now places those carats under this grade. Nothing is guessed:
-- the row keeps its own size, weight and rate, and it appears on the Stock page
-- under a name that says precisely what is known about it. Somebody who can
-- read the paper moves it to the real grade with an ADJUST, which is the same
-- correction path every other mistaken bucket takes (BR-INV-1).
--
-- Nothing is created by hand here. add_grade (0030) already does the whole job
-- -- the code, the display name, the sort order and the grade_size pairings
-- 0018's rule gives it -- and it is idempotent, so this file is safe to run
-- repeatedly and safe to run against a database that already has the grade.
-- ---------------------------------------------------------------------------

begin;

select public.add_grade('Unknown Grade', 'Unknown Grade');

commit;

-- ---------------------------------------------------------------------------
-- Verification · every row must read 'ok'.
-- ---------------------------------------------------------------------------
select 'the grade exists and is active' as case,
       case when exists (select 1 from public.grade
                          where code = 'Unknown Grade' and active)
            then 'ok' else 'MISSING' end as status
union all
select 'it is paired with every size the import can use',
       case when (select count(*) from public.grade_size gs
                    join public.grade g using (grade_id)
                   where g.code = 'Unknown Grade')
                 = (select count(*) from public.size_bucket where code <> '-2')
            then 'ok' else 'PAIRINGS MISSING' end
union all
select 'running it twice creates nothing',
       case when (public.add_grade('Unknown Grade') ->> 'created') = 'false'
            then 'ok' else 'CREATED A DUPLICATE' end;
