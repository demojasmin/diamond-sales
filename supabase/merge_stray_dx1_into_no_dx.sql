-- ---------------------------------------------------------------------------
-- Two "DX1" in every picker: move the lines off the stray "Dx1" row and remove it.
--
-- WHY THIS EXISTS, AND WHY 0053 DOES NOT DO IT. 0053 stops a new duplicate ever
-- appearing -- grade_key makes case, spacing and punctuation stop mattering, and
-- the printed marks became aliases -- but its closing note says outright that it
-- does not fold away a duplicate that was ALREADY created. It cannot: the two
-- rows may both hold movements, lines and prices, and which one keeps the carats
-- is the office's decision, not a migration's. This is that decision, made for
-- the one pair 0053's verification reports as STILL SPLIT.
--
-- WHAT WENT WRONG IN THE FIRST PLACE. The app shows the mark and the database
-- stores the code: GradeNames.Marks turns code "NO DX" into "DX1" on screen. The
-- importers did not use that map -- they resolve a sheet's grade names through
-- the aliases column -- so a sheet writing "DX1" did not match code "NO DX", the
-- importer offered to create the missing grade, and somebody said yes.
--
-- AND WHY THE ALIAS ALONE IS NOT ENOUGH. 0053 added "DX1" to NO DX's aliases,
-- and a CODE STILL BEATS AN ALIAS in ExcelImport.AliasMap. Measured on Demo
-- after 0053 was applied: nine of the ten DX spellings resolve to #6 NO DX and
-- "Dx1" typed exactly still resolves to #33, because #33's code is "Dx1". While
-- that row exists the split stands.
--
-- WHAT MOVES, AND WHAT DOES NOT. Only sales_line.grade_id. Measured on Demo
-- before this was written: the stray (id 33) carries 4 sales lines -- MIG-8 at
-- +6.5, and MIG-9 at -6.5, +11 and 1/6, 54.45 ct gross between them -- and
-- NOTHING else: no movement, no intake, no price row, no reservation, and not a
-- carat of position. So there is no ledger to rewrite and no cost basis to carry
-- across. The check below re-measures rather than trusting that, and refuses on
-- anything but sales lines, because a movement would need the carats moving too
-- and that is a different and much more careful script.
--
-- THE INVOICES DO NOT CHANGE. Not their weights, prices, buyers or numbers --
-- and not even what they PRINT: code "NO DX" displays as the mark "DX1", which
-- is what the stray was called. The only thing that changes is which catalogue
-- row each line points at, from a duplicate to the original.
--
-- WHAT IT DOES TO THE RECONCILIATION. Nothing, and that is worth saying. Those
-- four lines have no SALE movements against them either way, so they are short
-- before and short after -- they simply become short under NO DX instead of
-- under a second row nobody meant to have. v_reconciliation's Dx1 rows are
-- folded into NO DX's, they are not settled by this.
--
-- IT IS AUDITED. sales_line is one of the audited tables, so the repoint leaves
-- a trail with the old and new values on it. That is the reason to do it in SQL
-- rather than by editing the catalogue around it.
--
-- EITHER DATABASE. Set the guard below. Priya Sales has no stray "Dx1" row at
-- all -- measured -- in which case this reports that and changes nothing.
--
-- RUN 0053 FIRST. This script is the second half of that fix, not a substitute
-- for it.
-- ---------------------------------------------------------------------------

begin;

-- ── the guard · SET THIS FIRST ─────────────────────────────────────────────
--     Priya (handover) : Priya Sales     <- the client's live database
--     Demo             : Priya Gems      <- where the stray Dx1 row is
do $$
declare
    expected constant text := 'Priya Gems';   -- <<<< EDIT ME
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
    v_keep     bigint;
    v_stray    bigint;
    v_lines    integer;
    v_moves    integer;
    v_intake   integer;
    v_price    integer;
    v_holds    integer;
    v_unpaired text;
    v_alias    text;
begin
    select grade_id into v_keep  from public.grade where code = 'NO DX';

    -- By KEY, not by the exact spelling: the stray was created from whatever the
    -- sheet printed, so it may read "Dx1", "DX1" or "dx1". grade_key is 0053's,
    -- and it is what makes those one name. Excluding the grade being kept, and
    -- taking the oldest match, so the answer cannot depend on row order.
    select grade_id into v_stray
      from public.grade
     where public.grade_key(code) = public.grade_key('DX1')
       and grade_id <> coalesce(v_keep, -1)
     order by grade_id
     limit 1;

    if v_keep is null then
        raise exception 'No grade with code "NO DX" here. Nothing changed.';
    end if;

    if v_stray is null then
        raise notice 'No stray "DX1" row on this database. Only the alias is checked below.';
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
                'The stray DX1 grade (id %) carries more than sales lines: % movement(s), % intake(s), % price row(s), % reservation(s). Nothing changed -- this script only moves lines.',
                v_stray, v_moves, v_intake, v_price, v_holds;
        end if;

        -- Every size those lines use must already be paired with NO DX, or the merge
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
                'NO DX is not paired with size(s) % that these lines use. Pair them first, or the merged lines land on a bucket the catalogue does not carry. Nothing changed.',
                v_unpaired;
        end if;

        raise notice 'Moving % sales line(s) from the stray DX1 (id %) to "NO DX" (id %).',
                     v_lines, v_stray, v_keep;

        update public.sales_line set grade_id = v_keep where grade_id = v_stray;

        delete from public.grade_size where grade_id = v_stray;
        delete from public.grade      where grade_id = v_stray;
        raise notice 'Removed the stray DX1 row and its pairings.';
    end if;

    -- 0053 puts "DX1" in NO DX's aliases already. Kept here so this script also
    -- closes the hole on a database where 0053 has not been run yet, and so it
    -- says which of the two it found.
    select aliases into v_alias from public.grade where grade_id = v_keep;
    if not exists (
        select 1 from unnest(string_to_array(coalesce(v_alias, ''), ';')) as a
         where upper(btrim(a)) = 'DX1')
    then
        update public.grade
           set aliases = case when coalesce(btrim(v_alias), '') = '' then 'DX1'
                              else btrim(v_alias) || ';DX1' end
         where grade_id = v_keep;
        raise notice 'Added "DX1" to NO DX''s aliases.';
    else
        raise notice '"DX1" was already an alias of NO DX.';
    end if;
end $$;

commit;


-- ---------------------------------------------------------------------------
-- Verification. Every row must read 'ok'.
-- ---------------------------------------------------------------------------
select 'one row per DX spelling' as check,
       case when (select count(*) from public.grade
                   where public.grade_key(code) in (public.grade_key('DX1'),
                                                    public.grade_key('NO DX'))) = 1
            then 'ok' else 'STILL TWO ROWS' end as status

union all
select 'and 0053''s own check now agrees',
       case when (select count(distinct (public.add_grade(s.spelling) ->> 'grade_id'))
                    from unnest(array['DX', 'Dx', 'dx', 'DX1', 'Dx1', 'dx1', 'dX1',
                                      'NO DX', 'no-dx', 'NODX']) as s(spelling)) = 1
            then 'ok' else 'STILL SPLIT' end

union all
select 'no line was lost',
       case when (select count(*) from public.sales_line l
                    join public.grade g using (grade_id)
                   where g.code = 'NO DX') >= 4
            then 'ok' else 'LINES MISSING' end

union all
select 'every active grade can still be sold',
       case when (select count(*) from public.grade g
                   where g.active
                     and not exists (select 1 from public.grade_size s
                                      where s.grade_id = g.grade_id)) = 0
            then 'ok' else 'A GRADE HAS NO SIZES' end
 order by 1;

-- The lines themselves, so the invoices can be read back.
select i.invoice_no, i.invoice_date, z.code as size,
       l.gross_weight_ct, l.selection_ct, l.price_per_ct
  from public.sales_line l
  join public.sales_invoice i on i.invoice_id = l.invoice_id
  left join public.size_bucket z on z.size_id = l.size_id
 where l.grade_id = (select grade_id from public.grade where code = 'NO DX')
 order by i.invoice_no, z.code;
