-- ---------------------------------------------------------------------------
-- 0056. 1MB is sold in sieve -2.
--
-- 0018 seeded grade_size as "every size except -2, which is NO 1 and NO 1 BB only", and 0029 gave
-- 1MB the same rule. The client's own sheets say otherwise: their printed stock sheet carries a
-- 1MB -2 row, their rate sheet prices it (stock_sheet_rates "1MB,-2,42000"), and their sales file
-- sells it (sale upload (2).xlsx row 52, MIG-16). The stock import let it in -- the pairing
-- trigger is on sales_line only -- so Priya holds 10.95 ct of 1MB x -2 that no sale could take,
-- and a sales file naming it was refused whole: "Grade 1MB does not use size -2".
--
-- Adds that one pairing, and any other bucket that ALREADY holds stock without one, since stock
-- that cannot be sold is the same fault wherever it sits. Adds rows only; changes no stock.
-- Safe to run repeatedly.
-- ---------------------------------------------------------------------------
begin;

insert into public.grade_size (grade_id, size_id)
select g.grade_id, s.size_id
  from public.grade g join public.size_bucket s on s.code = '-2'
 where g.code = '1MB'
on conflict do nothing;

insert into public.grade_size (grade_id, size_id)
select distinct m.grade_id, m.size_id
  from public.stock_movement m
on conflict do nothing;

commit;

-- Verification. Both rows must read 'ok'.
select '1MB can be sold in -2' as check,
       case when exists (select 1 from public.grade_size gs
                           join public.grade g on g.grade_id = gs.grade_id
                           join public.size_bucket s on s.size_id = gs.size_id
                          where g.code = '1MB' and s.code = '-2')
            then 'ok' else 'MISSING' end as status
union all
select 'no stock sits in a bucket that cannot be sold',
       case when not exists (select 1 from public.stock_movement m
                              where not exists (select 1 from public.grade_size gs
                                                 where gs.grade_id = m.grade_id and gs.size_id = m.size_id))
            then 'ok' else 'STILL UNPAIRED' end;
