-- ---------------------------------------------------------------------------
-- 0044  The ledger balance, beside the available one
--
-- WHY. 0043 made balance_ct net of stock_reservation, which is right for the Stock page: what it
-- shows is what is left to sell. But the Stock REPORT is the office's sheet, and it prints
--     TOTAL STOCK  -  SALES  =  ON HAND
-- with TOTAL derived as on-hand plus sales. Reserved carats are in neither term, so the moment a
-- sales entry held 5 ct the report's TOTAL fell by 5 -- carats that physically exist, are not sold,
-- and appeared in no figure on the page. Holds now survive a restart, so a forgotten entry would
-- have understated TOTAL indefinitely with nothing on screen to explain it.
--
-- The desk's decision: the report is a LEDGER document and must not move when a parcel is spoken
-- for. So the view carries both figures and each screen reads the one it means.
--
--   balance_ct   what is AVAILABLE  -- movements less reservations.  Stock page.
--   ledger_ct    what is HELD       -- movements only.               Stock report.
--   reserved_ct  the difference, named, so the gap between them is never a mystery.
--
-- Nothing about the arithmetic changes. balance_ct is the same expression 0043 left; the two new
-- columns are the halves it was already computing. Adding columns to a view is safe for existing
-- readers -- PostgREST selects by name, and nothing selects *.
--
-- THE NEW COLUMNS GO LAST, and they have to: "create or replace view" may only APPEND to the
-- select list. Putting them beside balance_ct where they read best made Postgres see avg_cost
-- being renamed to ledger_ct and refuse the whole statement (42P16). Dropping the view instead
-- would have taken every dependent view with it, which is a far worse trade for tidier source.
-- ---------------------------------------------------------------------------

create or replace view public.v_stock_position as
with movement as (
    select
        m.grade_id,
        m.size_id,
        case when m.movement_type in ('INTAKE', 'CONVERT_IN') then m.weight_ct
             when m.movement_type = 'ADJUST'                  then m.weight_ct
             else -m.weight_ct end                            as signed_ct,
        case when m.movement_type in ('INTAKE', 'CONVERT_IN') then m.weight_ct
             when m.movement_type = 'ADJUST' and m.weight_ct > 0 then m.weight_ct
             else 0 end                                       as inward_ct,
        case when m.movement_type in ('INTAKE', 'CONVERT_IN')
             then m.weight_ct * coalesce(m.price_per_ct, 0)
             when m.movement_type = 'ADJUST' and m.weight_ct > 0
             then m.weight_ct * coalesce(m.price_per_ct, 0)
             else 0 end                                       as inward_value,
        case when m.movement_type in ('INTAKE', 'CONVERT_IN')
             then m.movement_date end                         as inward_date
    from public.stock_movement m
),
held as (
    select r.grade_id, r.size_id, sum(r.weight_ct) as reserved_ct
      from public.stock_reservation r
     group by r.grade_id, r.size_id
)
select
    g.grade_id,
    g.code                          as grade_code,
    g.display_name                  as grade_name,
    s.size_id,
    s.code                          as size_code,

    -- AVAILABLE. Unchanged from 0043.
    round(coalesce(sum(mv.signed_ct), 0) - coalesce(min(h.reserved_ct), 0), 4) as balance_ct,

    case when coalesce(sum(mv.inward_ct), 0) > 0
         then sum(mv.inward_value) / sum(mv.inward_ct)
         else 0 end                           as avg_cost,
    round(
        greatest(coalesce(sum(mv.signed_ct), 0) - coalesce(min(h.reserved_ct), 0), 0)
        * case when coalesce(sum(mv.inward_ct), 0) > 0
               then sum(mv.inward_value) / sum(mv.inward_ct)
               else 0 end
    , 2)                                      as stock_value,
    min(mv.inward_date)                       as oldest_intake,
    current_date - min(mv.inward_date)        as age_days,

    -- LAST, per the note above -- not because they belong here.
    --
    -- HELD. The movement ledger alone: what a stock take would count, whatever is spoken for.
    round(coalesce(sum(mv.signed_ct), 0), 4)                                   as ledger_ct,

    -- The difference, named. min() because the left join repeats the bucket's single held row
    -- across every movement row -- the same reason balance_ct uses it.
    round(coalesce(min(h.reserved_ct), 0), 4)                                  as reserved_ct
from public.grade g
cross join public.size_bucket s
left join movement mv on mv.grade_id = g.grade_id and mv.size_id = s.size_id
left join held h      on h.grade_id  = g.grade_id and h.size_id  = s.size_id
group by g.grade_id, g.code, g.display_name, s.size_id, s.code;

comment on view public.v_stock_position is
    'CALC-6/7. Balance, average cost and value per grade x size. balance_ct is what is AVAILABLE: movements less the carats a sales entry has spoken for (0043). ledger_ct is what is HELD: the movement ledger alone, which is what the Stock report prints so that a reservation never moves a ledger document (0044). reserved_ct is the difference between them.';
