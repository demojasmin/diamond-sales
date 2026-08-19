-- ---------------------------------------------------------------------------
-- 0023 · A conversion must not invent stock that cost nothing.
--
-- v_stock_position averages cost over INTAKE and CONVERT_IN rows:
--
--     sum(weight_ct * coalesce(price_per_ct, 0)) / sum(weight_ct)
--
-- so a CONVERT_IN written with a null or zero price adds its CARATS to the
-- denominator and NOTHING to the numerator. Converting 100 ct into a bucket
-- holding 100 ct at 40,000 halves its average to 20,000, and the carats were
-- never bought at zero -- they were bought at whatever the source bucket cost.
--
-- Harmless while the average was only ever displayed. Not harmless now: 0019
-- stamps that average onto sales_line.cost_per_ct when an invoice is posted,
-- and that figure is deliberately never recomputed. A diluted average becomes a
-- permanently wrong cost of goods, and a permanently wrong margin.
--
-- The fix is the accounting one rather than a validation one: carats do not
-- change value by moving between buckets, so a conversion with no stated price
-- INHERITS the source bucket's average cost. Nothing to type, nothing to get
-- wrong, and the total value of the book is unchanged by the move -- which is
-- what makes it a conversion rather than an intake.
--
-- Two cases still raise, because neither has an honest answer:
--   * an explicit zero or negative price -- carats are not free, and accepting
--     0 here is the exact dilution this migration exists to stop;
--   * a source bucket with no cost basis of its own, where there is nothing to
--     inherit. The message says so and asks for a price.
-- ---------------------------------------------------------------------------
create or replace function public.convert_stock(
    p_from_grade_id bigint,
    p_from_size_id  bigint,
    p_to_grade_id   bigint,
    p_to_size_id    bigint,
    p_weight_ct     numeric,
    p_price_per_ct  numeric default null,
    p_date          date    default current_date,
    p_client_ref    uuid    default null
)
returns jsonb
language plpgsql
as $$
declare
    v_warning text;
    v_price   numeric;
    v_from    text;
begin
    if p_weight_ct is null or p_weight_ct <= 0 then
        raise exception 'Conversion weight must be greater than zero';
    end if;

    if p_from_grade_id = p_to_grade_id and p_from_size_id = p_to_size_id then
        raise exception 'Cannot convert a parcel into itself';
    end if;

    -- Explicit zero is a mistake, not a price. Blank means "unchanged", which is
    -- handled below; typing 0 says the carats are worth nothing, and they are not.
    if p_price_per_ct is not null and p_price_per_ct <= 0 then
        raise exception 'A conversion price of % is not valid. Leave it blank to carry the source bucket''s cost, or enter what the carats are worth.',
            p_price_per_ct;
    end if;

    v_warning := public.assert_stock(p_from_grade_id, p_from_size_id, p_weight_ct);

    if p_price_per_ct is null then
        select nullif(avg_cost, 0), grade_code || ' x ' || size_code
          into v_price, v_from
          from public.v_stock_position
         where grade_id = p_from_grade_id and size_id = p_from_size_id;

        if v_price is null then
            raise exception 'No cost is recorded for %, so there is nothing for this conversion to carry. Enter a price per carat.',
                coalesce(v_from, 'that bucket');
        end if;
    else
        v_price := p_price_per_ct;
    end if;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct,
         ref_type, counterparty_grade_id, created_by, client_ref)
    values
        (p_date, p_from_grade_id, p_from_size_id, 'CONVERT_OUT', p_weight_ct,
         v_price, 'conversion', p_to_grade_id, auth.uid(), p_client_ref),
        (p_date, p_to_grade_id, p_to_size_id, 'CONVERT_IN', p_weight_ct,
         v_price, 'conversion', p_from_grade_id, auth.uid(), null);

    return jsonb_build_object('ok', true, 'warning', v_warning, 'price_per_ct', v_price);
end;
$$;
