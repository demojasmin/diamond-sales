-- ---------------------------------------------------------------------------
-- 0028 · Two things, and the second is why the first was needed.
--
--   1. Remove the test adjustments left in the ledger by probe runs and by
--      trying out the Stock report's edit mode. They are not corrections to
--      anything that happened; they are exercise weights nobody took off.
--
--   2. Let an adjustment carry the RATE the stock is worth, so correcting a
--      weight stops destroying the bucket's cost.
--
-- WHY (2) IS A BUG AND NOT A FEATURE
--
-- v_stock_position (0008) computes average cost over every INWARD movement:
--
--     avg_cost = sum(inward_value) / sum(inward_ct)
--
-- and a positive ADJUST contributes weight_ct * coalesce(price_per_ct, 0).
-- adjust_stock() has never set price_per_ct, so every upward correction added
-- carats worth NOTHING and dragged the average down. On this database:
--
--     NO 1 BB x 14+ : (7.72 x 58,000 + 20.00 x 0) / 27.72 = 16,152.96
--
-- against a true rate of 58,000. That is not only a wrong number on a report.
-- post_invoice (0019) stamps sales_line.cost_per_ct from this same avg_cost,
-- so a stock recount silently understates the cost of everything sold
-- afterwards and overstates margin. The Dashboard's Margin KPI reads it.
--
-- APPEND-ONLY, AND WHY (1) IS STILL A DELETE
--
-- BR-INV-1 says corrections are compensating movements, never deletions --
-- because the ledger is the record of what HAPPENED, and unhappening a real
-- event destroys the audit trail. These rows record nothing that happened.
-- A compensating -10 ct would leave the weight right and the COST still
-- wrong, because a negative adjustment does not reduce inward_ct: the zero
-- value is already averaged in and no later row can take it out. 0012 and
-- 0015 removed corrupt test data for the same reason and set the precedent.
--
-- Each row is named by id AND re-checked against its own text, so if these
-- ids mean something different on another database nothing is touched.
-- ---------------------------------------------------------------------------

do $$
declare
    v_ids     bigint[] := array[
        -- idempotency probe, +14 x 14+, six pairs that net to zero weight
        1186, 1188, 1189, 1191, 1192, 1194, 1199, 1201,
        -- idempotency probe, COL x +6.5
        2349, 2351, 2368, 2370, 2375, 2377,
        -- Stock report edit-mode probe, TOP-COL x 14+
        2382, 2383, 2384, 2385, 2455, 2456,
        -- trying out edit mode by hand, NO 1 BB x 14+, reason "no"
        2388, 2389,
        -- the run that PROVED this bug, COL x 14+: an un-rated +1 ct that took the
        -- bucket from 30,000 to 29,454 a carat, and the -2.5 ct that was meant to
        -- reverse a +2.5 the database had already refused. Removing both is what
        -- puts COL x 14+ back to the sheet's 53.97 ct at 30,000; compensating
        -- movements would fix the weight and leave the cost diluted for good.
        2569, 2570
    ];
    v_found   integer;
    v_deleted integer;
    v_net     numeric;
begin
    -- Only rows that are still what this migration was written against: a
    -- manual adjustment whose reason says it was a probe or a trial. A real
    -- correction that happened to land on one of these ids is left alone.
    select count(*), coalesce(sum(weight_ct), 0)
      into v_found, v_net
      from public.stock_movement
     where movement_id = any(v_ids)
       and movement_type = 'ADJUST'
       and ref_type = 'manual'
       and (reason ilike '%probe%' or reason ilike '%. no'
            or reason ilike 'Stock report correction: reversing%');

    raise notice 'matched % of % row(s), net weight %', v_found, array_length(v_ids, 1), v_net;

    delete from public.stock_movement
     where movement_id = any(v_ids)
       and movement_type = 'ADJUST'
       and ref_type = 'manual'
       and (reason ilike '%probe%' or reason ilike '%. no'
            or reason ilike 'Stock report correction: reversing%');

    get diagnostics v_deleted = row_count;
    raise notice 'removed % test adjustment(s)', v_deleted;
end;
$$;


-- ---------------------------------------------------------------------------
-- An adjustment that knows what the carats are worth.
--
-- A NEW NAME rather than two more arguments on adjust_stock: adding a
-- defaulted parameter creates a second overload instead of replacing the
-- first, and PostgREST then refuses both as ambiguous (PGRST203). Same reason
-- 0027 introduced import_stock beside replace_imported_stock.
--
-- p_price_per_ct matters only when the weight is POSITIVE. A negative
-- adjustment removes carats, and v_stock_position counts nothing outward
-- towards the average -- so a rate on one would be recorded and never read,
-- which is worse than not asking for it.
-- ---------------------------------------------------------------------------
create or replace function public.adjust_stock_at_cost(
    p_grade_id     bigint,
    p_size_id      bigint,
    p_weight_ct    numeric,
    p_price_per_ct numeric,
    p_reason       text,
    p_date         date default current_date,
    p_client_ref   uuid default null
)
returns jsonb
language plpgsql
as $$
declare
    v_warning text;
begin
    if p_weight_ct is null or p_weight_ct = 0 then
        raise exception 'An adjustment must move a non-zero weight';
    end if;

    if p_reason is null or btrim(p_reason) = '' then
        raise exception 'An adjustment reason is required';
    end if;

    if p_price_per_ct is not null and p_price_per_ct < 0 then
        raise exception 'A rate cannot be negative';
    end if;

    -- Adding carats at no stated rate is what diluted this ledger's cost in
    -- the first place. Refused rather than accepted quietly: the caller knows
    -- the rate, or the correction is not ready to be made.
    if p_weight_ct > 0 and coalesce(p_price_per_ct, 0) = 0 then
        raise exception 'Adding % ct needs the rate those carats are worth, or the average cost of this bucket is destroyed', p_weight_ct;
    end if;

    if p_weight_ct < 0 then
        v_warning := public.assert_stock(p_grade_id, p_size_id, -p_weight_ct);
    end if;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, reason, created_by, client_ref)
    values
        (p_date, p_grade_id, p_size_id, 'ADJUST', p_weight_ct,
         case when p_weight_ct > 0 then p_price_per_ct end,
         'manual', btrim(p_reason), auth.uid(), p_client_ref);

    return jsonb_build_object('ok', true, 'warning', v_warning);
end;
$$;

revoke all on function public.adjust_stock_at_cost(bigint, bigint, numeric, numeric, text, date, uuid) from public;
grant execute on function public.adjust_stock_at_cost(bigint, bigint, numeric, numeric, text, date, uuid) to authenticated;
