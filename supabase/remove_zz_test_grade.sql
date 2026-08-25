-- ---------------------------------------------------------------------------
-- Remove the ZZ TEST grade from Demo, and sweep the catalogue for duplicates.
--
-- The delete is guarded four ways: the row must be id 29, it must be spelled
-- ZZ TEST, and it must carry no sales line and no stock movement. If any of
-- those is untrue the script raises and deletes nothing, rather than quietly
-- removing zero rows and letting you believe it worked.
--
-- Unknown Grade is deliberately left alone: PdfStockImport depends on it.
-- ---------------------------------------------------------------------------

begin;

do $$
declare
    v_code  text;
    v_lines integer;
    v_moves integer;
    v_pairs integer;
begin
    select g.code,
           (select count(*) from public.sales_line     l where l.grade_id = g.grade_id),
           (select count(*) from public.stock_movement m where m.grade_id = g.grade_id),
           (select count(*) from public.grade_size     s where s.grade_id = g.grade_id)
      into v_code, v_lines, v_moves, v_pairs
      from public.grade g
     where g.grade_id = 29;

    if v_code is null then
        raise exception 'There is no grade with id 29. Nothing removed.';
    end if;

    if v_code <> 'ZZ TEST' then
        raise exception 'Grade 29 is "%", not "ZZ TEST". Nothing removed -- check the id first.', v_code;
    end if;

    if v_lines > 0 or v_moves > 0 then
        raise exception 'ZZ TEST carries % sales line(s) and % movement(s). Nothing removed: a grade holding trade is not test data.',
                        v_lines, v_moves;
    end if;

    raise notice 'ZZ TEST verified: 0 sales lines, 0 movements, % grade_size pairing(s).', v_pairs;
end $$;

delete from public.grade_size where grade_id = 29;
delete from public.grade      where grade_id = 29;

commit;


-- ── did it go, and is Unknown Grade untouched? ─────────────────────────────
select 'ZZ TEST gone'          as check,
       case when not exists (select 1 from public.grade where code = 'ZZ TEST')
            then 'ok' else 'STILL THERE' end as status
union all
select 'Unknown Grade kept',
       case when exists (select 1 from public.grade where code = 'Unknown Grade')
            then 'ok' else 'MISSING -- the PDF importer needs it' end
union all
select 'grades now', (select count(*)::text from public.grade)
union all
select 'no grade lost its pairings',
       case when (select count(*) from public.grade g
                   where g.active
                     and not exists (select 1 from public.grade_size s
                                      where s.grade_id = g.grade_id)) = 0
            then 'ok'
            else (select string_agg(g.code, ', ') from public.grade g
                   where g.active
                     and not exists (select 1 from public.grade_size s
                                      where s.grade_id = g.grade_id)) end
 order by 1;
