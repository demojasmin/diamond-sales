-- ---------------------------------------------------------------------------
-- Two "GH VS" in every picker: move the lines off the stray and remove it.
--
-- THIS IS THE MERGE. merge_stray_gh_vs_grade.sql handles the easy case -- a
-- stray that carries nothing -- and REFUSES when it carries trade, which is what
-- it does on Priya. This is that refusal answered: the lines are repointed at the
-- real grade first, and only then is the row taken out.
--
-- WHY THERE ARE TWO. The app shows the mark and the database stores the code:
-- GradeNames.Marks turns code "GH" into "GH VS" on screen. The IMPORT does not
-- use that map -- it resolves a sheet's grade names through the aliases column --
-- so a sheet writing "GH VS" did not match code "GH", the importer offered to
-- create the missing grade, and somebody said yes. Both now read "GH VS" in every
-- picker, and the Stock filter can only tell them apart by printing the code in
-- brackets.
--
-- WHAT MOVES, AND WHAT DOES NOT. Only sales_line.grade_id. Measured on Priya
-- before this was written: the stray (id 31) carries 2 sales lines and NOTHING
-- else -- no movement, no intake, no price row, no reservation -- so there is no
-- ledger to rewrite and no cost basis to carry across. The check below
-- re-measures and refuses on anything but sales lines, because a movement would
-- need the carats moving too and that is a different script.
--
-- THE INVOICE DOES NOT CHANGE. Not its weight, its price, its buyer or its
-- number -- and not even what it PRINTS: code "GH" displays as the mark "GH VS",
-- which is what the stray was called. The only thing that changes is which
-- catalogue row the line points at, from a duplicate to the original.
--
-- IT IS AUDITED. sales_line is one of the audited tables, so the repoint leaves a
-- trail with the old and new values on it. That is the reason to do it in SQL
-- rather than by editing the catalogue around it.
--
-- AND THE ALIAS, so it cannot happen again: "GH VS" is added to GH's aliases if
-- missing. Without it the next import offers to create the row a second time --
-- and while the row exists a CODE BEATS AN ALIAS in ExcelImport.AliasMap, so the
-- alias alone was never enough.
-- ---------------------------------------------------------------------------

begin;

-- ── the guard · SET THIS FIRST ─────────────────────────────────────────────
--     Priya (handover) : Priya Sales
--     Demo             : Priya Gems
do $$
declare
    expected constant text := 'Priya Sales';   -- <<<< EDIT ME
    v text;
begin
    select value into v from public.app_config where key = 'company_name';
    if v is distinct from expected then
        raise exception
            'Refusing: this run is set for "%" and this database says "%". Nothing changed.',
            expected, coalesce(v, '(none)');
    end if;
    raise notice 'Database confirmed: %.', expected;
end $$;


do $$
declare
    v_keep    bigint;
    v_stray   bigint;
    v_lines   integer;
    v_moves   integer;
    v_intake  integer;
    v_price   integer;
    v_holds   integer;
    v_unpaired text;
    v_alias   text;
begin
    select grade_id into v_keep  from public.grade where code = 'GH';
    select grade_id into v_stray from public.grade where code = 'GH VS';

    if v_keep is null then
        raise exception 'No grade with code "GH" here. Nothing changed.';
    end if;

    if v_stray is null then
        raise notice 'No stray "GH VS" row on this database. Only the alias is checked below.';
    else
        select count(*) into v_lines  from public.sales_line        where grade_id = v_stray;
        select count(*) into v_moves  from public.stock_movement
                                      where grade_id = v_stray or counterparty_grade_id = v_stray;
        select count(*) into v_intake from public.rough_intake      where grade_id = v_stray;
        select count(*) into v_price  from public.price_list        where grade_id = v_stray;
        select count(*) into v_holds  from public.stock_reservation where grade_id = v_stray;

        -- Sales lines are the ONLY thing this moves. Anything else means carats or
        -- money are attached, and those need deciding on rather than repointing.
        if v_moves + v_intake + v_price + v_holds > 0 then
            raise exception
                'Grade "GH VS" (id %) carries more than sales lines: % movement(s), % intake(s), % price row(s), % reservation(s). Nothing changed -- this script only moves lines.',
                v_stray, v_moves, v_intake, v_price, v_holds;
        end if;

        -- Every size those lines use must already be paired with GH, or the merge
        -- puts a line on a grade x size the catalogue does not carry -- which the
        -- Stock page would show as a bucket that cannot exist.
        select string_agg(distinct z.code, ', ') into v_unpaired
          from public.sales_line l
          join public.size_bucket z on z.size_id = l.size_id
         where l.grade_id = v_stray
           and not exists (select 1 from public.grade_size s
                            where s.grade_id = v_keep and s.size_id = l.size_id);

        if v_unpaired is not null then
            raise exception
                'GH is not paired with size(s) % that these lines use. Pair them first, or the merged lines land on a bucket the catalogue does not carry. Nothing changed.',
                v_unpaired;
        end if;

        raise notice 'Moving % sales line(s) from "GH VS" (id %) to "GH" (id %).',
                     v_lines, v_stray, v_keep;

        update public.sales_line set grade_id = v_keep where grade_id = v_stray;

        delete from public.grade_size where grade_id = v_stray;
        delete from public.grade      where grade_id = v_stray;
        raise notice 'Removed the "GH VS" row and its pairings.';
    end if;

    select aliases into v_alias from public.grade where grade_id = v_keep;
    if not exists (
        select 1 from unnest(string_to_array(coalesce(v_alias, ''), ';')) as a
         where upper(btrim(a)) = 'GH VS')
    then
        update public.grade
           set aliases = case when coalesce(btrim(v_alias), '') = '' then 'GH VS'
                              else btrim(v_alias) || ';GH VS' end
         where grade_id = v_keep;
        raise notice 'Added "GH VS" to GH''s aliases.';
    else
        raise notice '"GH VS" was already an alias of GH.';
    end if;
end $$;

commit;


-- ---------------------------------------------------------------------------
-- Verification. One row, carrying the lines and the alias.
-- ---------------------------------------------------------------------------
select g.code, g.display_name, g.sort_order, g.aliases,
       (select count(*) from public.sales_line l where l.grade_id = g.grade_id) as sales_lines
  from public.grade g
 where g.code in ('GH', 'GH VS')
 order by g.code;

-- The lines themselves, so the invoices can be read back.
select l.line_id, i.invoice_no, i.invoice_date, z.code as size,
       l.gross_weight_ct, l.selection_ct, l.price_per_ct
  from public.sales_line l
  join public.sales_invoice i on i.invoice_id = l.invoice_id
  left join public.size_bucket z on z.size_id = l.size_id
 where l.grade_id = (select grade_id from public.grade where code = 'GH')
 order by i.invoice_no;

-- Nothing may be left unreachable.
select g.code as grade_with_no_sizes
  from public.grade g
 where g.active and not exists (select 1 from public.grade_size s where s.grade_id = g.grade_id)
 order by 1;
