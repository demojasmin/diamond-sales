-- ---------------------------------------------------------------------------
-- 0014 · The negative-stock policy could never block, and two of the four
--        stock writes never consulted it at all.
--
-- 1 · The policy lookup missed, twice over. The RPCs read app_config key
--     'negative_stock'; DiamondApi/Seed.cs seeds 'negative_stock_policy'. And
--     the settings screen documents the values as BLOCK / WARN / ALLOW while
--     the RPCs compared against lower-case 'block'. So an owner who set BLOCK
--     got neither the block nor the warning: v_policy matched no branch at all
--     and post_invoice fell straight through to the insert. Setting the policy
--     to its strictest value made the system LESS safe than leaving it unset,
--     because an unset key at least defaulted to 'warn'.
--
-- 2 · Rejection and adjustment were client-side inserts into stock_movement
--     with no balance check in the client, the RPC layer or the schema. Only
--     conversion and posting ever looked at the balance. Rejecting 500 ct from
--     a bucket holding 10 ct posted silently and left it at -490.
--
-- Both are fixed at the root rather than per caller: one function that reads
-- the policy, one that enforces it, and every write that takes carats out goes
-- through them.
--
-- Idempotent and safe to re-run. No data is written or altered.
-- ---------------------------------------------------------------------------

-- ---------------------------------------------------------------------------
-- The policy, normalised. Either key name, any casing, whitespace tolerated.
-- WARN stays the default, which is what an absent key already meant.
-- ---------------------------------------------------------------------------
create or replace function public.negative_stock_policy()
returns text
language sql
stable
as $$
    select lower(btrim(coalesce(
        (select value from public.app_config where key = 'negative_stock'),
        (select value from public.app_config where key = 'negative_stock_policy'),
        'warn')));
$$;

comment on function public.negative_stock_policy() is
    'block / warn / allow, lower-cased, read from either key spelling. '
    'warn is the default, as an absent key has always meant.';

-- ---------------------------------------------------------------------------
-- The guard. Raises under the block policy; otherwise hands back a sentence
-- for the caller to show, or null when there is enough stock.
--
-- Returning the warning rather than raising it is what lets warn mean
-- something at last: before this, every caller either blocked or said nothing.
-- ---------------------------------------------------------------------------
create or replace function public.assert_stock(
    p_grade_id  bigint,
    p_size_id   bigint,
    p_weight_ct numeric
)
returns text
language plpgsql
as $$
declare
    v_balance numeric;
    v_grade   text;
    v_size    text;
    v_policy  text;
begin
    -- Only an outward move can overdraw a bucket.
    if p_weight_ct is null or p_weight_ct <= 0 then
        return null;
    end if;

    v_policy := public.negative_stock_policy();

    -- ALLOW means allow, silently. Reading the balance to say nothing about it would only
    -- cost a query on every write.
    if v_policy not in ('block', 'warn') then
        return null;
    end if;

    select balance_ct, grade_code, size_code
      into v_balance, v_grade, v_size
      from public.v_stock_position
     where grade_id = p_grade_id and size_id = p_size_id;

    if coalesce(v_balance, 0) >= p_weight_ct then
        return null;
    end if;

    if v_policy = 'block' then
        raise exception 'Only % ct on hand for % x %, cannot take out % ct',
            coalesce(v_balance, 0), coalesce(v_grade, '?'), coalesce(v_size, '?'), p_weight_ct;
    end if;

    return format('This takes %s x %s below zero - %s ct on hand, %s ct going out.',
                  coalesce(v_grade, '?'), coalesce(v_size, '?'),
                  coalesce(v_balance, 0), p_weight_ct);
end;
$$;

-- ---------------------------------------------------------------------------
-- POST · unchanged from 0010 except the policy read, which now goes through
-- negative_stock_policy() so that BLOCK and WARN are recognised whatever their
-- casing and whichever key holds them. The three-way block / warn+override /
-- allow behaviour is deliberately untouched.
-- ---------------------------------------------------------------------------
create or replace function public.post_invoice(
    p_invoice_id bigint,
    p_override   boolean default false
)
returns jsonb
language plpgsql
as $$
declare
    v_status text;
    v_date   date;
    v_policy text;
    v_short  jsonb;
    v_no     varchar(20);
begin
    select status, invoice_date into v_status, v_date
      from public.sales_invoice
     where invoice_id = p_invoice_id;

    -- Not found and not-visible-to-you are the same thing under RLS.
    if not found then
        raise exception 'Invoice % not found', p_invoice_id using errcode = 'no_data_found';
    end if;

    -- Idempotent: replaying a queued post must not deduct the stock twice.
    if v_status = 'POSTED' then
        return jsonb_build_object('ok', true, 'already_posted', true);
    end if;

    if v_status = 'CANCELLED' then
        raise exception 'Invoice % is cancelled and cannot be posted', p_invoice_id;
    end if;

    if not exists (select 1 from public.sales_line where invoice_id = p_invoice_id) then
        raise exception 'Invoice % has no lines', p_invoice_id;
    end if;

    v_policy := public.negative_stock_policy();

    -- The whole parcel leaves stock: selection as SALE, rejection as REJECTION.
    -- Their sum is gross_weight_ct, so that is what the balance must cover.
    with need as (
        select grade_id, size_id, sum(gross_weight_ct) as out_ct
          from public.sales_line
         where invoice_id = p_invoice_id
         group by grade_id, size_id
    )
    select jsonb_agg(jsonb_build_object(
               'grade_code', sp.grade_code,
               'size_code',  sp.size_code,
               'balance_ct', sp.balance_ct,
               'needed_ct',  n.out_ct))
      into v_short
      from need n
      join public.v_stock_position sp
        on sp.grade_id = n.grade_id and sp.size_id = n.size_id
     where sp.balance_ct < n.out_ct;

    if v_short is not null then
        if v_policy = 'block' then
            raise exception 'Posting would take stock negative: %', v_short::text;
        elsif v_policy = 'warn' and not p_override then
            -- Nothing written. The client asks, then calls back with override.
            return jsonb_build_object('ok', false, 'needs_override', true,
                                      'shortfalls', v_short);
        end if;
    end if;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select v_date, l.grade_id, l.size_id, 'SALE', l.selection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid()
      from public.sales_line l
     where l.invoice_id = p_invoice_id and l.selection_ct > 0;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select v_date, l.grade_id, l.size_id, 'REJECTION', l.rejection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid()
      from public.sales_line l
     where l.invoice_id = p_invoice_id and l.rejection_ct > 0;

    -- The number and the POSTED status land together, in this transaction.
    -- A draft that is never posted never consumes one.
    update public.sales_invoice
       set status     = 'POSTED',
           invoice_no = coalesce(invoice_no, public.next_invoice_no(v_date)),
           updated_by = auth.uid()
     where invoice_id = p_invoice_id;

    select invoice_no into v_no from public.sales_invoice where invoice_id = p_invoice_id;
    return jsonb_build_object('ok', true, 'invoice_no', v_no);
end;
$$;

-- ---------------------------------------------------------------------------
-- CONVERT · unchanged from 0010 except that the hand-rolled balance check is
-- replaced by assert_stock, so it honours BLOCK whatever the casing and now
-- returns the warning instead of swallowing it.
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
begin
    if p_weight_ct is null or p_weight_ct <= 0 then
        raise exception 'Conversion weight must be greater than zero';
    end if;

    if p_from_grade_id = p_to_grade_id and p_from_size_id = p_to_size_id then
        raise exception 'Cannot convert a parcel into itself';
    end if;

    v_warning := public.assert_stock(p_from_grade_id, p_from_size_id, p_weight_ct);

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct,
         ref_type, counterparty_grade_id, created_by, client_ref)
    values
        (p_date, p_from_grade_id, p_from_size_id, 'CONVERT_OUT', p_weight_ct,
         p_price_per_ct, 'conversion', p_to_grade_id, auth.uid(), p_client_ref),
        (p_date, p_to_grade_id, p_to_size_id, 'CONVERT_IN', p_weight_ct,
         p_price_per_ct, 'conversion', p_from_grade_id, auth.uid(), null);

    return jsonb_build_object('ok', true, 'warning', v_warning);
end;
$$;

-- ---------------------------------------------------------------------------
-- REJECT · was a bare client-side insert. Same shape as convert_stock now:
-- validated, guarded, and idempotent through client_ref.
-- ---------------------------------------------------------------------------
create or replace function public.record_rejection(
    p_grade_id     bigint,
    p_size_id      bigint,
    p_weight_ct    numeric,
    p_price_per_ct numeric default null,
    p_date         date    default current_date,
    p_client_ref   uuid    default null
)
returns jsonb
language plpgsql
as $$
declare
    v_warning text;
begin
    if p_weight_ct is null or p_weight_ct <= 0 then
        raise exception 'Rejection weight must be greater than zero';
    end if;

    v_warning := public.assert_stock(p_grade_id, p_size_id, p_weight_ct);

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct,
         ref_type, created_by, client_ref)
    values
        (p_date, p_grade_id, p_size_id, 'REJECTION', p_weight_ct, p_price_per_ct,
         'manual', auth.uid(), p_client_ref);

    return jsonb_build_object('ok', true, 'warning', v_warning);
end;
$$;

-- ---------------------------------------------------------------------------
-- ADJUST · signed since 0008, so only the downward half can overdraw. The
-- reason stays mandatory; 0009 enforces it in the schema as well.
-- ---------------------------------------------------------------------------
create or replace function public.adjust_stock(
    p_grade_id   bigint,
    p_size_id    bigint,
    p_weight_ct  numeric,
    p_reason     text,
    p_date       date default current_date,
    p_client_ref uuid default null
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

    if p_weight_ct < 0 then
        v_warning := public.assert_stock(p_grade_id, p_size_id, -p_weight_ct);
    end if;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         ref_type, reason, created_by, client_ref)
    values
        (p_date, p_grade_id, p_size_id, 'ADJUST', p_weight_ct,
         'manual', btrim(p_reason), auth.uid(), p_client_ref);

    return jsonb_build_object('ok', true, 'warning', v_warning);
end;
$$;

grant execute on function public.negative_stock_policy()                to authenticated;
grant execute on function public.assert_stock(bigint, bigint, numeric)  to authenticated;
grant execute on function public.record_rejection(bigint, bigint, numeric, numeric, date, uuid)
                                                                        to authenticated;
grant execute on function public.adjust_stock(bigint, bigint, numeric, text, date, uuid)
                                                                        to authenticated;
