-- ---------------------------------------------------------------------------
-- The 2.79 ct that would not come out of stock: re-file it into the sieve the
-- sales sheet actually sells from.
--
--
-- WHAT THE INVESTIGATION FOUND, so this is not taken on trust
--
-- THE IMPORT IS NOT AT FAULT. Measured against the workbook: 51 of 51 rows read,
-- every row's weight equal to selection + rejection with no exceptions, every
-- grade and every sieve resolved (including Excel's raw 0.16666666666666666 for
-- 1/6), and gross deducted exactly as post_invoice deducts it for a sale typed
-- into the app -- SALE for the selection, REJECTION for the rejection. The sheet
-- says 1,209.2600 ct gross; the ledger took 1,206.4700. The 2.7900 difference is
-- 0051's cap doing its job on three lines, not arithmetic going wrong.
--
-- THE STOCK SHEET IS AN OPENING POSITION, not a closing count. It covers the
-- sales in 36 of the 39 buckets the sheet sells from, with 1,001.2000 ct to
-- spare. A count taken after these sales would be short nearly everywhere.
--
-- THE THREE SHORT BUCKETS ARE A SIEVE DISAGREEMENT, not a shortage. Each sits
-- inside a grade with hundreds of carats spare:
--
--     NO II   x 1/6   short 1.5500   grade has   390.0300 ct spare
--     NO 2    x -6.5  short 0.7500   grade has    72.2300 ct spare
--     NO 1 BB x -2    short 0.4900   grade has   212.0800 ct spare
--
-- And the stock sheet counts a 0.2 sieve that the sales sheet NEVER sells from,
-- at any grade: 218.6200 ct sitting idle across 18 grades. The sales workbook's
-- sieve vocabulary simply has no 0.2 in it.
--
-- PRICE SAYS WHICH SIEVE THE GOODS REALLY CAME FROM. A sieve's price per carat
-- is characteristic of that sieve:
--
--   NO 1 BB x -2, MIG-16 at 62,000/ct. That grade's stock costs -6.5 36,693,
--   +6.5 38,794, +11 46,962, 0.2 63,000, 0.25 68,000, 1/6 58,000. The line is
--   priced like a 0.2 parcel and nothing like a -6.5 one -- and the count has no
--   -2 row at all while 0.2 holds 8.2100 ct nobody has sold.
--
--   NO II x 1/6, sold at 51,875/ct average. That grade's stock costs 1/6 47,000
--   and 0.2 51,000. The sales are priced above their own sieve's cost and level
--   with 0.2, which holds 21.5100 ct nobody has sold.
--
--   NO 2 x -6.5, MIG-9 at 22,355/ct against a -6.5 cost of 21,276. THIS ONE'S
--   SIEVE IS RIGHT -- the price matches it and matches nothing else. So its
--   0.7500 ct comes from the nearest-cost neighbour, +6.5 at 27,677, and
--   deliberately NOT from 0.2 at 40,500, which would distort a cheap bucket.
--
--
-- WHY A CONVERSION AND NOT AN ADJUSTMENT OR AN INVOICE CHANGE
--
--   an ADJUSTMENT would invent 2.79 ct that demonstrably already exist in 0.2,
--   and leave the total position overstated by that much;
--
--   correcting the SALES SHEET would be the most truthful answer if the row were
--   known, but the 1.5500 ct on NO II is spread across four lines and choosing
--   one would be a guess written onto an issued invoice;
--
--   a CONVERSION moves carats that are already counted, changes no document, and
--   leaves the total position untouched. It is also reversible -- convert back.
--
-- Through convert_stock (0010), not a hand-written pair of movements: the RPC
-- applies the negative-stock policy, writes the CONVERT_OUT and CONVERT_IN
-- together, and lands on the audit trail. Every source bucket has far more than
-- is being taken, so the 'block' policy this database runs cannot refuse it.
--
-- COST CARRIED FROM THE SOURCE, because that is what those carats cost when they
-- were counted.
--
--
-- AFTER THIS, RE-IMPORT THE SALES FILE. The conversion alone does not settle it:
-- the three lines are already capped in the ledger, and only a re-import rewrites
-- their movements. replace_imported_sales deletes what it wrote before, so the
-- sheet deducts once, in full. Expected afterwards: carats taken out 1,209.2600,
-- no shortfall note, all three buckets at 0.0000, reconciliation clean.
--
-- SAFE TO RUN ONCE. Running it twice would move another 2.79 ct; the check at the
-- end reports the position so a second run is obvious.
-- ---------------------------------------------------------------------------

begin;

-- ── the guard ──────────────────────────────────────────────────────────────
--     Priya (handover) : "Priya Sales"     <- this script is set for this one
--     Demo             : "Priya Gems"
do $$
declare v text;
begin
    select value into v from public.app_config where key = 'company_name';
    if v is distinct from 'Priya Sales' then
        raise exception
            'Refusing: this correction is for the Priya handover database (company_name = "Priya Sales"). This one says "%".',
            coalesce(v, '(none)');
    end if;
    raise notice 'Database confirmed: Priya Sales.';
end $$;


do $$
declare
    r        record;
    v_from_g bigint;
    v_from_s bigint;
    v_to_g   bigint;
    v_to_s   bigint;
    v_have   numeric;
    v_cost   numeric;
begin
    -- grade, from sieve, to sieve, carats. The three rows the investigation named.
    for r in
        select * from (values
            ('NO II',   '0.2',  '1/6',  1.5500::numeric),
            ('NO 1 BB', '0.2',  '-2',   0.4900::numeric),
            ('NO 2',    '+6.5', '-6.5', 0.7500::numeric)
        ) as t(grade, from_size, to_size, carats)
    loop
        select grade_id into v_from_g from public.grade where code = r.grade;
        select size_id  into v_from_s from public.size_bucket where code = r.from_size;
        select size_id  into v_to_s   from public.size_bucket where code = r.to_size;
        v_to_g := v_from_g;                       -- same grade, different sieve

        if v_from_g is null or v_from_s is null or v_to_s is null then
            raise exception 'Catalogue is missing % or one of its sieves % / %',
                r.grade, r.from_size, r.to_size;
        end if;

        -- Refuse rather than overdraw the bucket the carats come OUT of.
        select coalesce(balance_ct, 0) into v_have
          from public.v_stock_position
         where grade_id = v_from_g and size_id = v_from_s;

        if coalesce(v_have, 0) < r.carats then
            raise exception '% x % holds only % ct, cannot move % ct out of it',
                r.grade, r.from_size, coalesce(v_have, 0), r.carats;
        end if;

        -- What those carats cost where they were counted, so the move carries its
        -- own cost rather than inventing one.
        select case when sum(i.weight_ct) > 0
                    then round(sum(i.weight_ct * i.price_per_ct) / sum(i.weight_ct), 2)
               end
          into v_cost
          from public.rough_intake i
         where i.grade_id = v_from_g and i.size_id = v_from_s;

        raise notice 'Moving % ct of % from sieve % to sieve % at % per ct.',
                     r.carats, r.grade, r.from_size, r.to_size, coalesce(v_cost, 0);

        perform public.convert_stock(
            p_from_grade_id => v_from_g,
            p_from_size_id  => v_from_s,
            p_to_grade_id   => v_to_g,
            p_to_size_id    => v_to_s,
            p_weight_ct     => r.carats,
            p_price_per_ct  => v_cost);
    end loop;
end $$;

commit;


-- ---------------------------------------------------------------------------
-- Verification. Every row must read 'ok'.
-- ---------------------------------------------------------------------------
select 'the three buckets can now cover their sales' as check,
       case when (
           select count(*) from (
               select l.grade_id, l.size_id,
                      sum(l.gross_weight_ct)                        as sells,
                      max(coalesce(p.balance_ct, 0) + coalesce(m.moved, 0)) as room
                 from public.sales_line l
                 join public.sales_invoice i on i.invoice_id = l.invoice_id
                 left join public.v_stock_position p
                        on p.grade_id = l.grade_id and p.size_id = l.size_id
                 left join lateral (
                        select sum(sm.weight_ct) as moved
                          from public.stock_movement sm
                         where sm.ref_type = 'sales_line'
                           and sm.grade_id = l.grade_id and sm.size_id = l.size_id
                      ) m on true
                where i.invoice_no like 'MIG-%'
                group by l.grade_id, l.size_id
               having sum(l.gross_weight_ct) >
                      max(coalesce(p.balance_ct, 0) + coalesce(m.moved, 0)) + 0.00005
           ) short) = 0
            then 'ok' else 'STILL SHORT - re-read the notices above' end as status

union all
select 'no carats were created or destroyed',
       case when (select round(sum(weight_ct), 4) from public.stock_movement
                   where movement_type = 'CONVERT_IN')
               = (select round(sum(weight_ct), 4) from public.stock_movement
                   where movement_type = 'CONVERT_OUT')
            then 'ok' else 'CONVERSION IS UNBALANCED' end

union all
select 'nothing went below zero',
       case when (select count(*) from public.v_stock_position where balance_ct < 0) = 0
            then 'ok' else 'A BUCKET IS NEGATIVE' end
 order by 1;

-- The three buckets, before the re-import. Each should now hold the carats its
-- capped line could not take.
select g.code as grade, z.code as size,
       round(p.ledger_ct, 4) as holds_now
  from public.v_stock_position p
  join public.grade g       on g.grade_id = p.grade_id
  join public.size_bucket z on z.size_id  = p.size_id
 where (g.code, z.code) in (('NO II', '1/6'), ('NO 2', '-6.5'), ('NO 1 BB', '-2'),
                            ('NO II', '0.2'), ('NO 1 BB', '0.2'), ('NO 2', '+6.5'))
 order by g.code, z.code;

-- The whole position. Unchanged by a conversion: it moves carats, it does not
-- add or remove any.
select round(sum(ledger_ct), 4) as ledger_ct,
       round(sum(balance_ct), 4) as available_ct
  from public.v_stock_position;


-- ---------------------------------------------------------------------------
-- NOW RE-IMPORT "sale upload (1).xlsx" (Settings -> Import sales from Excel).
--
-- Until that is done the three lines are still capped in the ledger and the
-- Invoices page will still disagree with Stock by 2.79 ct. The re-import removes
-- the movements it wrote before and writes them again against the corrected
-- position, so nothing is deducted twice.
--
-- Expect: 18 invoices, 51 lines, carats taken out 1,209.2600 ct, and NO
-- "could not be taken out of stock" note at all.
-- ---------------------------------------------------------------------------
