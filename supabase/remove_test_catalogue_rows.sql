-- ---------------------------------------------------------------------------
-- Take the four rows nobody trades out of the catalogue.
--
-- GOES:  grade "ZZ TEST"        test data
--        grade "Unknown Grade"  the PDF importer's holding pen
--        grade "+14"            a grade the office does not trade
--        size  "14+"            a sieve the office does not trade
--
-- THE GRADE AND THE SIZE ARE DIFFERENT ROWS with the characters transposed --
-- grade "+14", size "14+" -- and both go. Worth stating because reading one for
-- the other is the easiest mistake on this page: grade_size says +14 traded in
-- +14/+18/+23 and nobody else did, so the grade and its sieves are one story.
--
-- They were already hidden from the SALE pickers (Catalogue.NotForSale), so the
-- desk could not write an invoice against them -- but they still appeared in the
-- Stock filters, the Master data grids and every report. This removes the rows,
-- which is what "remove them from the application" actually means.
--
-- EITHER DATABASE. Set the guard at the top for the one you mean. The rows are
-- matched by CODE, not by id, because the ids differ between the two -- 29 is
-- Unknown Grade on Priya and ZZ TEST on Demo, which is exactly the sort of thing
-- an id-based script gets wrong quietly. (The older remove_zz_test_grade.sql IS
-- id-based and is Demo-only for that reason.)
--
-- ON DEMO IT WILL PROBABLY REFUSE, and that is the script working. Demo has
-- traded: if any of these four carries a movement or a sales line, removing it
-- would take real history with it. Run the preflight below first and decide what
-- to do about the attachments before touching the catalogue.
--
-- ONE TRANSACTION, and it refuses rather than half-does. Every target is checked
-- for attached trade first: if any of them carries a sales line, a movement, an
-- intake parcel or a live reservation, the whole script raises and deletes
-- nothing. A catalogue row holding history is not test data, whatever it is
-- called.
--
-- READ THIS BEFORE RUNNING IT
--
--   Unknown Grade is LOAD-BEARING for the printed-sheet importer. PdfStockImport
--   lands every row that printed no grade name on it (0039), which is what stops
--   an unnamed row being dropped and the sheet's own totals failing to
--   reconcile. Removing it is recoverable and not silent: the importer falls
--   through to the ordinary unknown-grade path and OFFERS TO CREATE IT BY NAME
--   on the next import that needs it. See PdfStockImport.UnknownGrade.
--
--   On PRIYA that is safe: the ledger was emptied by
--   reset_priya_for_handover.sql, so the row holds nothing. On DEMO it may hold
--   carats, and then the check below refuses and you have a real decision rather
--   than a cleanup.
--
--   Either way it is not free: the first PDF import with a blank grade column
--   will ask to add "Unknown Grade" back. Say yes -- it is the same row, and the
--   alternative is losing the carats on that line.
-- ---------------------------------------------------------------------------

begin;

-- ── the guard · SET THIS FIRST ─────────────────────────────────────────────
-- Both databases hold a company that trades diamonds, and the tab you are in is
-- not evidence of which. Name the one you MEAN, and the script refuses to run
-- anywhere else.
--
--     Priya (handover) : Priya Sales
--     Demo             : Priya Gems
--
-- Editing this line is not a way round the guard -- it is the guard. It turns
-- "whichever tab I happen to be in" into a decision that has to be typed, and
-- then checks the database agrees with it.
do $$
declare
    expected constant text := 'Priya Sales';   -- <<<< EDIT ME
    v text;
begin
    select value into v from public.app_config where key = 'company_name';
    if v is distinct from expected then
        raise exception
            'Refusing: this run is set for "%" and this database says "%". Nothing removed.',
            expected, coalesce(v, '(none)');
    end if;
    raise notice 'Database confirmed: %.', expected;
end $$;


-- ── nothing may be attached ────────────────────────────────────────────────
-- Counted BEFORE anything is deleted, and reported per row, because "it removed
-- nothing" and "there was nothing to remove" look identical afterwards and mean
-- opposite things.
do $$
declare
    r        record;
    v_lines  integer;
    v_moves  integer;
    v_intake integer;
    v_price  integer;
    v_holds  integer;
    v_pairs  integer;
    v_found  integer := 0;
begin
    for r in
        select 'grade'::text as kind, g.grade_id as id, g.code
          from public.grade g
         where g.code in ('ZZ TEST', 'Unknown Grade', '+14')
        union all
        select 'size', z.size_id, z.code
          from public.size_bucket z
         where z.code = '14+'
    loop
        v_found := v_found + 1;

        if r.kind = 'grade' then
            select count(*) into v_lines  from public.sales_line     where grade_id = r.id;
            select count(*) into v_moves  from public.stock_movement
                                          where grade_id = r.id or counterparty_grade_id = r.id;
            select count(*) into v_intake from public.rough_intake   where grade_id = r.id;
            select count(*) into v_price  from public.price_list     where grade_id = r.id;
            select count(*) into v_holds  from public.stock_reservation where grade_id = r.id;
            select count(*) into v_pairs  from public.grade_size     where grade_id = r.id;
        else
            select count(*) into v_lines  from public.sales_line     where size_id = r.id;
            select count(*) into v_moves  from public.stock_movement where size_id = r.id;
            select count(*) into v_intake from public.rough_intake   where size_id = r.id;
            select count(*) into v_price  from public.price_list     where size_id = r.id;
            select count(*) into v_holds  from public.stock_reservation where size_id = r.id;
            select count(*) into v_pairs  from public.grade_size     where size_id = r.id;
        end if;

        if v_lines + v_moves + v_intake + v_price + v_holds > 0 then
            raise exception
                '% "%" (id %) carries trade: % sales line(s), % movement(s), % intake(s), % price row(s), % reservation(s). Nothing removed.',
                r.kind, r.code, r.id, v_lines, v_moves, v_intake, v_price, v_holds;
        end if;

        raise notice '% "%" (id %): clean, % pairing(s) will go with it.',
                     r.kind, r.code, r.id, v_pairs;
    end loop;

    if v_found = 0 then
        raise exception 'None of the four rows is in this catalogue. Nothing to remove.';
    end if;

    raise notice '% of 4 target row(s) found and clean.', v_found;
end $$;


-- ── the pairings first, then the rows they point at ────────────────────────
delete from public.grade_size
 where grade_id in (select grade_id from public.grade where code in ('ZZ TEST', 'Unknown Grade', '+14'))
    or size_id  in (select size_id  from public.size_bucket where code = '14+');

delete from public.grade       where code in ('ZZ TEST', 'Unknown Grade', '+14');
delete from public.size_bucket where code = '14+';

commit;


-- ---------------------------------------------------------------------------
-- Verification. The first block must read "gone"; the second must not be empty.
-- ---------------------------------------------------------------------------
select 'grade ZZ TEST' as row,
       case when exists (select 1 from public.grade where code = 'ZZ TEST')
            then 'STILL THERE' else 'gone' end as status
union all
select 'grade Unknown Grade',
       case when exists (select 1 from public.grade where code = 'Unknown Grade')
            then 'STILL THERE' else 'gone' end
union all
select 'grade +14',
       case when exists (select 1 from public.grade where code = '+14')
            then 'STILL THERE' else 'gone' end
union all
select 'size 14+',
       case when exists (select 1 from public.size_bucket where code = '14+')
            then 'STILL THERE' else 'gone' end
 order by 1;

select 'grades left'   as kept, count(*)::text from public.grade
union all select 'sizes left',    count(*)::text from public.size_bucket
union all select 'pairings left', count(*)::text from public.grade_size;

-- NO ACTIVE GRADE MAY BE LEFT WITH NOWHERE TO TRADE. A grade whose only sieve
-- was 14+ would still be in every picker and pick nothing, which reads as a
-- broken screen rather than as a catalogue that lost a row. Must be empty.
select g.code as grade_with_no_sizes
  from public.grade g
 where g.active
   and not exists (select 1 from public.grade_size s where s.grade_id = g.grade_id)
 order by 1;

-- AND THE OTHER WAY ROUND. +14 was the only grade trading +18 and +23, so taking
-- it out can leave those sieves paired with nothing -- still in the Stock filter
-- and in Master data, reachable from no grade's line. Not a fault, and not
-- something to fix blind: a sieve the office still counts into belongs in the
-- catalogue whether or not a grade is currently paired with it. Reported so the
-- decision is made rather than discovered.
select z.code as size_no_grade_trades
  from public.size_bucket z
 where z.active
   and not exists (select 1 from public.grade_size s where s.size_id = z.size_id)
 order by 1;
