-- ===========================================================================
-- The write paths that must be atomic (§5.1).
--
-- PostgREST has no client-side transaction. `supabase-csharp` cannot wrap two
-- calls in one, so this is broken and cannot be fixed on the client:
--
--     await client.From<SalesInvoice>().Update(...);   -- POSTED
--     await client.From<StockMovement>().Insert(...);  -- network drops here
--
-- The result is an invoice marked POSTED with no stock deducted: v_reconciliation
-- goes non-zero, the balance is silently wrong, and nobody notices until
-- month-end. Each function below is one statement from the client's point of
-- view and therefore one transaction.
--
-- SECURITY INVOKER throughout, deliberately. A SECURITY DEFINER function would
-- run as its owner and let any signed-in user post anybody's invoice, bypassing
-- every policy in 0003. As invoker, RLS still decides: a salesperson can post
-- their own DRAFT (invoice_update allows created_by = auth.uid() and status =
-- 'DRAFT') and insert movements (movement_insert allows is_staff), and cannot
-- touch anyone else's.
-- ===========================================================================

-- ---------------------------------------------------------------------------
-- invoice_no · assigned HERE, at post, never by the client (docs/03 §2.3).
-- Drafts carry NULL — 0009 makes that legal. Per-year series, zero-padded:
-- INV-2026-00001. The MIG- prefix is reserved for imported history so migrated
-- numbers can never collide with the live series (docs/08 §4).
--
-- advisory lock rather than a Postgres sequence: sequences leak numbers on
-- rollback, and gaps in an accounting document series invite exactly the audit
-- questions this system exists to answer. The lock is transaction-scoped, so it
-- releases with the post that took it.
-- ---------------------------------------------------------------------------
create or replace function public.next_invoice_no(p_date date default current_date)
returns varchar(20)
language plpgsql
as $$
declare
    v_year int := extract(year from p_date);
    v_seq  int;
begin
    perform pg_advisory_xact_lock(hashtext('invoice_no' || v_year));

    select coalesce(max(substring(invoice_no from 10)::int), 0) + 1
      into v_seq
      from public.sales_invoice
     where invoice_no like 'INV-' || v_year || '-%';

    return 'INV-' || v_year || '-' || lpad(v_seq::text, 5, '0');
end;
$$;

-- ---------------------------------------------------------------------------
-- POST · status -> POSTED, plus the SALE and REJECTION movements, atomically.
--
-- Returns jsonb rather than raising on a soft failure, because CFG-003's "warn"
-- policy is a question for the user, not an error:
--   { ok: true }                                    posted
--   { ok: true,  already_posted: true }             replay of a lost response
--   { ok: false, needs_override: true, shortfalls } warn policy, nothing written
-- and it raises only when the answer is a hard no (block policy, cancelled).
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

    select value into v_policy from public.app_config where key = 'negative_stock';
    v_policy := coalesce(v_policy, 'warn');

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
-- CANCEL · status -> CANCELLED, and give the carats back.
--
-- The original SALE and REJECTION rows are NOT deleted — the ledger is
-- append-only and the history is the point. Compensating ADJUST rows put the
-- weight back, which is only possible because 0008 made ADJUST signed.
-- ---------------------------------------------------------------------------
create or replace function public.cancel_invoice(
    p_invoice_id bigint,
    p_reason     text default null
)
returns jsonb
language plpgsql
as $$
declare
    v_status text;
begin
    select status into v_status
      from public.sales_invoice where invoice_id = p_invoice_id;

    if not found then
        raise exception 'Invoice % not found', p_invoice_id using errcode = 'no_data_found';
    end if;

    if v_status = 'CANCELLED' then
        return jsonb_build_object('ok', true, 'already_cancelled', true);
    end if;

    -- FR-SALES-12: the reason is mandatory, and 0009 enforces it on the ADJUST rows.
    if p_reason is null or btrim(p_reason) = '' then
        raise exception 'Cancelling an invoice requires a reason';
    end if;

    -- Only a POSTED invoice moved stock, so only that one needs reversing.
    if v_status = 'POSTED' then
        insert into public.stock_movement
            (movement_date, grade_id, size_id, movement_type, weight_ct,
             price_per_ct, ref_type, ref_id, created_by, reason)
        select current_date, m.grade_id, m.size_id, 'ADJUST', m.weight_ct,
               m.price_per_ct, 'cancel', p_invoice_id, auth.uid(),
               'Reversal of invoice ' || p_invoice_id || ': ' || p_reason
          from public.stock_movement m
          join public.sales_line l on l.line_id = m.ref_id
         where m.ref_type = 'sales_line'
           and l.invoice_id = p_invoice_id
           and m.movement_type in ('SALE', 'REJECTION');
    end if;

    update public.sales_invoice
       set status = 'CANCELLED', updated_by = auth.uid()
     where invoice_id = p_invoice_id;

    return jsonb_build_object('ok', true, 'reason', p_reason);
end;
$$;

-- ---------------------------------------------------------------------------
-- CONVERT · the "…ma thi avelu" grade-to-grade transfer (INV-004).
--
-- CONVERT_OUT and CONVERT_IN must land together or carats vanish from the
-- ledger with no trace of where they went.
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
    v_balance numeric;
    v_policy  text;
begin
    if p_weight_ct is null or p_weight_ct <= 0 then
        raise exception 'Conversion weight must be greater than zero';
    end if;

    if p_from_grade_id = p_to_grade_id and p_from_size_id = p_to_size_id then
        raise exception 'Cannot convert a parcel into itself';
    end if;

    select value into v_policy from public.app_config where key = 'negative_stock';

    select balance_ct into v_balance
      from public.v_stock_position
     where grade_id = p_from_grade_id and size_id = p_from_size_id;

    if coalesce(v_policy, 'warn') = 'block' and coalesce(v_balance, 0) < p_weight_ct then
        raise exception 'Only % ct on hand, cannot convert % ct', v_balance, p_weight_ct;
    end if;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct,
         ref_type, counterparty_grade_id, created_by, client_ref)
    values
        (p_date, p_from_grade_id, p_from_size_id, 'CONVERT_OUT', p_weight_ct,
         p_price_per_ct, 'conversion', p_to_grade_id, auth.uid(), p_client_ref),
        (p_date, p_to_grade_id, p_to_size_id, 'CONVERT_IN', p_weight_ct,
         p_price_per_ct, 'conversion', p_from_grade_id, auth.uid(), null);

    return jsonb_build_object('ok', true);
end;
$$;

grant execute on function public.next_invoice_no(date)                    to authenticated;
grant execute on function public.post_invoice(bigint, boolean)            to authenticated;
grant execute on function public.cancel_invoice(bigint, text)             to authenticated;
grant execute on function public.convert_stock(bigint, bigint, bigint, bigint,
                                               numeric, numeric, date, uuid) to authenticated;
