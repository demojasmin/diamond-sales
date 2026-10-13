-- ---------------------------------------------------------------------------
-- 0059. A stock increase always carries a cost.
--
-- adjust_stock takes no rate, and it accepted a positive weight: those carats entered the
-- average cost as free stones and dragged it down (Priya, 12 Oct: four "Repair Loss" rows of
-- +0.19 ct in all, no rate). adjust_stock_at_cost (0028) already refuses an increase without a
-- rate; the Intake page's Adjust box did not use it. The app now asks for a rate and sends
-- increases to adjust_stock_at_cost -- this closes the old door for every client, including an
-- older installed app.
--
-- Only the positive-weight case changes: it is refused with a sentence saying what to do.
-- Removals behave exactly as before. Existing rows are not touched.
-- ---------------------------------------------------------------------------
begin;

create or replace function public.adjust_stock(p_grade_id bigint, p_size_id bigint, p_weight_ct numeric,
                                               p_reason text, p_date date default current_date,
                                               p_client_ref uuid default null::uuid)
 returns jsonb
 language plpgsql
as $function$
declare
    v_warning text;
begin
    if p_weight_ct is null or p_weight_ct = 0 then
        raise exception 'An adjustment must move a non-zero weight';
    end if;
    if p_reason is null or btrim(p_reason) = '' then
        raise exception 'An adjustment reason is required';
    end if;
    -- 0059: an increase needs the rate those carats are worth -- adjust_stock_at_cost takes it.
    if p_weight_ct > 0 then
        raise exception 'Adding % ct needs the rate those carats are worth, or the average cost of this bucket is destroyed. Enter a rate per carat.', p_weight_ct;
    end if;
    v_warning := public.assert_stock(p_grade_id, p_size_id, -p_weight_ct);
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct, ref_type, reason, created_by, client_ref)
    values
        (p_date, p_grade_id, p_size_id, 'ADJUST', p_weight_ct, 'manual', btrim(p_reason), auth.uid(), p_client_ref);
    return jsonb_build_object('ok', true, 'warning', v_warning);
end;
$function$;

commit;
