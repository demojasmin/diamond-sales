-- ---------------------------------------------------------------------------
-- 0019 · Capture what a sold parcel COST, at the moment it is posted.
--
-- Nothing recorded cost before this. post_invoice stamped the outgoing movements
-- with l.price_per_ct -- the SELLING price -- so the ledger knew what every
-- parcel fetched and nothing about what it was worth to us.
--
-- The only cost signal available is v_stock_position.avg_cost, and it cannot be
-- read after the fact: it is a running weighted average over every intake ever
-- recorded, with no time bound. It moves whenever stock arrives and the stock
-- import rewrites it wholesale. Asked today what a March parcel cost, it answers
-- with today's average. So the figure has to be STAMPED at post time and never
-- recomputed -- that, and nothing else, is what makes historical margin stable.
--
-- Two columns on sales_line rather than a new table: the line already carries
-- the grade, size and carats, so cost belongs beside them, and immutability
-- comes free from simply never updating the column again.
--
-- NOT backfilled, on purpose. Migrated MIG- invoices deliberately write no stock
-- movements (the opening balance is already net of those sales), so they have no
-- cost basis and never can. Stamping today's average onto a 2025 invoice would
-- fabricate a margin that never existed. They stay null and are reported as
-- uncovered -- see 0020's cost_coverage.
-- ---------------------------------------------------------------------------
alter table public.sales_line
    add column if not exists cost_per_ct numeric(14,2),
    add column if not exists cost_basis  text;

comment on column public.sales_line.cost_per_ct is 'Weighted-average cost per carat at the moment the invoice was posted. Stamped once by post_invoice() and never recomputed. Null means no cost basis exists (migrated invoices, or a grade never taken in).';

comment on column public.sales_line.cost_basis is
    'How cost_per_ct was arrived at, so a later change of method is auditable rather than silent.';

-- ---------------------------------------------------------------------------
-- post_invoice, unchanged except for the cost stamp.
--
-- The stamp reads v_stock_position BEFORE the SALE and REJECTION rows are
-- written. Ordering is not strictly required -- avg_cost counts INTAKE and
-- CONVERT_IN only, so outward movements cannot move it -- but reading first
-- makes the intent legible and survives someone later widening what avg_cost
-- considers inward.
--
-- Cost is charged on gross_weight_ct, not selection_ct: the whole parcel left
-- stock, and the rejected carats are as much a cost of that sale as the sold
-- ones. Charging selection only would flatter every margin.
--
-- nullif(sp.avg_cost, 0) -- a zero average means "never taken in", not "free".
-- Stamping 0 would report a 100% margin on a parcel whose cost is simply
-- unknown, which is the one failure mode this whole feature must not have.
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

    -- ── the cost stamp · 0019 ──────────────────────────────────────────────
    update public.sales_line l
       set cost_per_ct = nullif(sp.avg_cost, 0),
           cost_basis  = case when nullif(sp.avg_cost, 0) is null
                              then null else 'moving_average' end
      from public.v_stock_position sp
     where l.invoice_id = p_invoice_id
       and sp.grade_id  = l.grade_id
       and sp.size_id   = l.size_id;

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
