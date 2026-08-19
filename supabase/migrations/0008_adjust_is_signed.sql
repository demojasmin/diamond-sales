-- ===========================================================================
-- ADJUST becomes signed — without this, cancel_invoice cannot exist.
--
-- THE PROBLEM
-- 0001 constrains every movement to `weight_ct >= 0`, and 0002 puts ADJUST in
-- the negative branch of v_stock_position:
--
--     case when movement_type in ('INTAKE','CONVERT_IN')
--          then weight_ct else -weight_ct end
--
-- So an ADJUST row can only ever REDUCE stock. But cancelling a posted invoice
-- has to give the carats back, and 0001's own comment says to "correct a
-- mistake with an offsetting ADJUST row" — which only works if the row can
-- offset in both directions. The stock-adjustment screen has the same gap: a
-- positive correction is currently unrecordable.
--
-- THE FIX
-- ADJUST carries its own sign; every other type keeps its positive-magnitude
-- meaning and its existing direction. The check is relaxed for ADJUST only, so
-- a negative INTAKE or SALE is still rejected.
--
-- NOTE FOR THE ANDROID APP (§7): no column is added, removed or renamed, so
-- nothing breaks. What changes is the MEANING of an ADJUST row — previously
-- always a decrease, now signed. Any client that assumed ADJUST subtracts
-- should be re-checked. As of this migration no ADJUST rows exist, so there is
-- no historic data to reinterpret.
-- ===========================================================================

alter table public.stock_movement
    drop constraint if exists stock_movement_weight_ct_check;

alter table public.stock_movement
    add constraint stock_movement_weight_ct_check
    check (weight_ct >= 0 or movement_type = 'ADJUST');

-- ---------------------------------------------------------------------------
-- Rebuilt with ADJUST signed. Everything else is byte-identical to 0002.
-- ---------------------------------------------------------------------------
create or replace view public.v_stock_position as
with movement as (
    select
        m.grade_id,
        m.size_id,
        -- ADJUST contributes exactly what is stored, sign included.
        case when m.movement_type in ('INTAKE', 'CONVERT_IN') then m.weight_ct
             when m.movement_type = 'ADJUST'                  then m.weight_ct
             else -m.weight_ct end                            as signed_ct,
        -- Cost basis follows the carats: a positive ADJUST is stock arriving
        -- back and carries its price, a negative one is stock leaving.
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
)
select
    g.grade_id,
    g.code                          as grade_code,
    g.display_name                  as grade_name,
    s.size_id,
    s.code                          as size_code,
    round(coalesce(sum(mv.signed_ct), 0), 4)  as balance_ct,          -- CALC-7
    case when coalesce(sum(mv.inward_ct), 0) > 0
         then sum(mv.inward_value) / sum(mv.inward_ct)
         else 0 end                           as avg_cost,            -- CALC-6
    round(
        greatest(coalesce(sum(mv.signed_ct), 0), 0)
        * case when coalesce(sum(mv.inward_ct), 0) > 0
               then sum(mv.inward_value) / sum(mv.inward_ct)
               else 0 end
    , 2)                                      as stock_value,
    min(mv.inward_date)                       as oldest_intake,
    current_date - min(mv.inward_date)        as age_days
from public.grade g
cross join public.size_bucket s
left join movement mv on mv.grade_id = g.grade_id and mv.size_id = s.size_id
group by g.grade_id, g.code, g.display_name, s.size_id, s.code;

alter view public.v_stock_position set (security_invoker = on);
grant select on public.v_stock_position to authenticated;
revoke all on public.v_stock_position from anon;
