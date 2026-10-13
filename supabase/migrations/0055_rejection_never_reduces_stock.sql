-- ---------------------------------------------------------------------------
-- 0055. Only SOLD carats leave stock. A rejection is a record, never a deduction.
--
-- APPLY AFTER 0054. Back the database up first (tests/probes/tool-backup-tables.cs.txt).
-- ROLLBACK: supabase/ROLLBACK_0055.sql.
--
--
-- THE RULE
--
-- A sale line is gross = selection + rejection. Until now every path took the
-- WHOLE gross out of the bucket: post_invoice and the sales import wrote a SALE
-- for the selection and a REJECTION for the rest, and v_stock_position
-- subtracted both. From here on only the SALE leaves stock:
--
--   v_stock_position      a REJECTION row counts 0. It is still in the ledger,
--                         still listed on the Rejections screen, still on the
--                         invoice -- it just no longer moves the balance.
--   post_invoice          a bucket must cover the SELECTION, not the gross.
--   edit_posted_invoice   the same, and an imported invoice is capped on its
--                         selection (0051's rule, now on what actually leaves).
--   replace_imported_sales  caps and reports on the selection; the rejection is
--                         written in full as a record.
--   cancel_invoice        gives back the SALE only -- the rejection was never
--                         taken, so crediting it would be a credit for nothing.
--   record_rejection      no longer checks or moves stock.
--
-- This matches the specification: INV-005 / docs/03 §345 make a REJECTION
-- movement optional behind auto_reject_on_post, default FALSE, and DiamondApi
-- deducts the selection only. The Supabase functions always deducted both.
--
--
-- HISTORY IS NOT ADDED BACK
--
-- Every REJECTION already in the ledger took carats out under the old rule.
-- Simply ignoring them would put all of it back at once (439.90 ct on Priya)
-- with nobody having decided that it should. So, in this same transaction, each
-- of those deductions is restated as an explicit ADJUST:
--
--   ref_type 'rejection_hold'  one row per invoice x bucket, ref_id = invoice_id
--   ref_type 'rej_hold_mvmt'   one row per REJECTION not tied to an invoice line
--                              (recorded by hand), ref_id = that movement_id
--
-- Every bucket's balance is therefore IDENTICAL before and after this
-- migration; the last step checks that and rolls everything back if a single
-- bucket differs. The holds are what an approved correction later removes,
-- bucket by bucket or invoice by invoice -- see
-- supabase/REJECTION_CORRECTION_PLAN.md. Until then the screen shows exactly
-- what it showed before.
--
-- How the holds behave afterwards, so nothing is credited twice:
--   * cancelling an invoice that has a hold releases that hold too, which is
--     exactly what cancelling it did before 0055 (the whole gross came back);
--   * updating an invoice leaves its hold alone (the hold is keyed by invoice,
--     not by line, so it survives the lines being rewritten);
--   * a sales re-import does not delete holds (it only deletes its own
--     'sales_line' movements), so re-importing cannot add history back either.
--
-- Nothing else changes: conversions, adjustments, intake, stock imports,
-- reservations, release-on-post, reconciliation, numbering, costing.
-- ---------------------------------------------------------------------------

begin;

-- ── 0 · the position as it stands, to prove it does not move ────────────────
create temporary table m0055_before on commit drop as
select grade_id, size_id, ledger_ct from public.v_stock_position;


-- ── 1 · restate every existing rejection deduction as an explicit hold ───────
-- Only on the FIRST run: a second run must not hold rejections written after 0055.
do $$
begin
    if exists (select 1 from public.stock_movement
                where ref_type in ('rejection_hold', 'rej_hold_mvmt')) then
        raise notice '0055: holds already exist, not created again';
        return;
    end if;

    -- trg_reject_retired_size refuses any new row on a retired sieve, and a hold IS a new row.
    -- Said plainly up front rather than failing half way (it would roll back either way).
    if exists (select 1 from public.stock_movement m
                 join public.size_bucket s on s.size_id = m.size_id
                where m.movement_type = 'REJECTION' and not s.active) then
        raise exception '0055: some rejections are on a retired sieve, so their hold cannot be written. '
                        'Restore those sieves on Master data, apply 0055, then retire them again. Nothing was applied.';
    end if;

    -- Rejections off an invoice line: one hold per invoice x bucket.
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct, ref_type, ref_id, reason)
    select min(m.movement_date), m.grade_id, m.size_id, 'ADJUST', -sum(m.weight_ct),
           'rejection_hold', l.invoice_id,
           '0055: rejection deducted under the old rule, kept out of stock pending review (invoice '
           || coalesce(max(i.invoice_no), l.invoice_id::text) || ')'
      from public.stock_movement m
      join public.sales_line l    on l.line_id = m.ref_id
      join public.sales_invoice i on i.invoice_id = l.invoice_id
     where m.movement_type = 'REJECTION' and m.ref_type = 'sales_line'
     group by l.invoice_id, m.grade_id, m.size_id
    having sum(m.weight_ct) <> 0;

    -- Every other rejection (recorded by hand, or a line that no longer exists): one hold each.
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct, ref_type, ref_id, reason)
    select m.movement_date, m.grade_id, m.size_id, 'ADJUST', -m.weight_ct,
           'rej_hold_mvmt', m.movement_id,
           '0055: rejection deducted under the old rule, kept out of stock pending review (movement '
           || m.movement_id || ')'
      from public.stock_movement m
     where m.movement_type = 'REJECTION'
       and m.weight_ct <> 0
       and not (m.ref_type = 'sales_line'
                and exists (select 1 from public.sales_line l where l.line_id = m.ref_id));
end;
$$;


-- ── 2 · the position: a REJECTION row no longer moves the balance ───────────
-- 0044's view, column for column; the only change is the REJECTION arm of signed_ct.
create or replace view public.v_stock_position as
with movement as (
    select
        m.grade_id,
        m.size_id,
        case when m.movement_type in ('INTAKE', 'CONVERT_IN') then m.weight_ct
             when m.movement_type = 'ADJUST'                  then m.weight_ct
             when m.movement_type = 'REJECTION'               then 0      -- 0055: a record, not a deduction
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
    round(coalesce(sum(mv.signed_ct), 0), 4)                                   as ledger_ct,
    round(coalesce(min(h.reserved_ct), 0), 4)                                  as reserved_ct
from public.grade g
cross join public.size_bucket s
left join movement mv on mv.grade_id = g.grade_id and mv.size_id = s.size_id
left join held h      on h.grade_id  = g.grade_id and h.size_id  = s.size_id
group by g.grade_id, g.code, g.display_name, s.size_id, s.code;

comment on view public.v_stock_position is
    'CALC-6/7. Balance, average cost and value per grade x size. 0055: only SOLD carats leave stock -- a REJECTION movement is a record and counts 0; rejections deducted before 0055 are held as explicit ADJUST rows (ref_type rejection_hold / rej_hold_mvmt). balance_ct is AVAILABLE (net of sales-entry reservations, 0043); ledger_ct is HELD (0044); reserved_ct is the difference.';


-- ── 3 · posting: the bucket must cover what is SOLD ─────────────────────────
-- 0054's body; the need is the selection, not the gross.
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
    v_short  jsonb;
    v_no     varchar(20);
begin
    select status, invoice_date into v_status, v_date
      from public.sales_invoice
     where invoice_id = p_invoice_id;

    if not found then
        raise exception 'Invoice % not found', p_invoice_id using errcode = 'no_data_found';
    end if;

    if v_status = 'POSTED' then
        return jsonb_build_object('ok', true, 'already_posted', true);
    end if;

    if v_status = 'CANCELLED' then
        raise exception 'Invoice % is cancelled and cannot be posted', p_invoice_id;
    end if;

    if not exists (select 1 from public.sales_line where invoice_id = p_invoice_id) then
        raise exception 'Invoice % has no lines', p_invoice_id;
    end if;

    -- 0055. What LEAVES the bucket is the selection; the rejection stays in it.
    with need as (
        select grade_id, size_id, sum(selection_ct) as out_ct
          from public.sales_line
         where invoice_id = p_invoice_id
         group by grade_id, size_id
        having sum(selection_ct) > 0
    )
    select jsonb_agg(jsonb_build_object(
               'grade_code', sp.grade_code,
               'size_code',  sp.size_code,
               'balance_ct', greatest(sp.balance_ct, 0),
               'needed_ct',  n.out_ct))
      into v_short
      from need n
      join public.v_stock_position sp
        on sp.grade_id = n.grade_id and sp.size_id = n.size_id
     where sp.balance_ct < n.out_ct;

    -- 0054. Whatever the policy, and whatever p_override says.
    if v_short is not null then
        raise exception 'Posting would take stock negative: %', v_short::text
            using errcode = 'check_violation';
    end if;

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

    -- The rejection is still written: it is the record the Rejections screen and the history read.
    -- 0055: it no longer changes the balance (see v_stock_position).
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select v_date, l.grade_id, l.size_id, 'REJECTION', l.rejection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid()
      from public.sales_line l
     where l.invoice_id = p_invoice_id and l.rejection_ct > 0;

    update public.sales_invoice
       set status     = 'POSTED',
           invoice_no = coalesce(invoice_no, public.next_invoice_no(v_date)),
           updated_by = auth.uid()
     where invoice_id = p_invoice_id;

    select invoice_no into v_no from public.sales_invoice where invoice_id = p_invoice_id;
    return jsonb_build_object('ok', true, 'invoice_no', v_no);
end;
$$;

comment on function public.post_invoice(bigint, boolean) is
    '0055. Posts a draft: per grade x size bucket, refuses when the bucket cannot cover the SELECTION (whatever the policy; p_override ignored), stamps cost, writes SALE (moves stock) and REJECTION (a record, moves nothing) and assigns the number. Supersedes 0054.';


-- ── 4 · correcting a posted invoice ─────────────────────────────────────────
-- 0054's body; the guard and the import cap are on the selection, and the rejection is written
-- in full as a record. Holds (ref_type rejection_hold) are keyed by invoice and left alone.
create or replace function public.edit_posted_invoice(
    p_invoice_id   bigint,
    p_invoice_date date,
    p_buyer_id     bigint,
    p_broker_id    bigint,
    p_broker_pct   numeric,
    p_terms_days   integer,
    p_doc_type     text,
    p_currency_id  bigint,
    p_lines        jsonb,
    p_reason       text
)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_status   text;
    v_no       text;
    v_short    text;
    v_reason   text := btrim(coalesce(p_reason, ''));
    v_note     text;
    v_imported boolean;
begin
    select status, invoice_no
      into v_status, v_no
      from public.sales_invoice
     where invoice_id = p_invoice_id
       for update;

    if not found then
        raise exception 'Invoice % not found', p_invoice_id using errcode = 'no_data_found';
    end if;

    if v_status = 'DRAFT' then
        raise exception 'Invoice % is a draft; save it the ordinary way', p_invoice_id;
    end if;

    if v_status = 'CANCELLED' then
        raise exception 'Invoice % is cancelled. Its stock has already been returned, so there is nothing to correct.', p_invoice_id;
    end if;

    if v_status <> 'POSTED' then
        raise exception 'Invoice % is %, which cannot be edited', p_invoice_id, v_status;
    end if;

    v_imported := coalesce(v_no, '') like 'MIG-%';

    if v_reason = '' then
        raise exception 'Correcting a posted invoice requires a reason';
    end if;

    if p_lines is null or jsonb_typeof(p_lines) <> 'array' or jsonb_array_length(p_lines) = 0 then
        raise exception 'An invoice must keep at least one line. To remove it entirely, cancel it.';
    end if;

    v_note := 'Edit of invoice ' || coalesce(v_no, p_invoice_id::text) || ': ' || v_reason;

    -- ── 1 · give the stock back ────────────────────────────────────────────
    delete from public.stock_movement m
     using public.sales_line l
     where m.ref_type = 'sales_line'
       and m.ref_id = l.line_id
       and l.invoice_id = p_invoice_id
       and m.movement_type in ('SALE', 'REJECTION');

    perform public.clear_import_edits(array[p_invoice_id]);

    -- ── 2 · the old lines go with them ─────────────────────────────────────
    delete from public.sales_line where invoice_id = p_invoice_id;

    -- ── 3 · the corrected lines ────────────────────────────────────────────
    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct,
         price_per_ct, ex_rate, less1_pct, less2_pct, remark)
    select p_invoice_id,
           (r->>'grade_id')::bigint,
           (r->>'size_id')::bigint,
           (r->>'gross_weight_ct')::numeric,
           (r->>'selection_ct')::numeric,
           (r->>'price_per_ct')::numeric,
           coalesce(nullif((r->>'ex_rate')::numeric, 0), 1),
           coalesce((r->>'less1_pct')::numeric, 0),
           coalesce((r->>'less2_pct')::numeric, 0),
           nullif(btrim(coalesce(r->>'remark', '')), '')
      from jsonb_array_elements(p_lines) as r;

    -- ── 4 · an ordinary invoice: the bucket covers what is SOLD, or nothing changes ──
    if not v_imported then
        select string_agg(format('Insufficient stock for %s x %s: required %s ct, available %s ct.',
                                 sp.grade_code, sp.size_code,
                                 trim_scale(round(n.out_ct, 4)),
                                 trim_scale(round(greatest(sp.balance_ct, 0), 4))), ' ')
          into v_short
          from (select grade_id, size_id, sum(selection_ct) as out_ct
                  from public.sales_line
                 where invoice_id = p_invoice_id
                 group by grade_id, size_id
                having sum(selection_ct) > 0) n
          join public.v_stock_position sp
            on sp.grade_id = n.grade_id and sp.size_id = n.size_id
         where sp.balance_ct < n.out_ct;

        if v_short is not null then
            raise exception '%', v_short using errcode = 'check_violation';
        end if;
    end if;

    -- ── 5 · the cost stamp, as posting does it (0019); imported lines stay uncosted ──
    update public.sales_line l
       set cost_per_ct = nullif(sp.avg_cost, 0),
           cost_basis  = case when nullif(sp.avg_cost, 0) is null
                              then null else 'moving_average' end
      from public.v_stock_position sp
     where l.invoice_id = p_invoice_id
       and not v_imported
       and sp.grade_id  = l.grade_id
       and sp.size_id   = l.size_id;

    -- ── 6 · and the stock leaves again ─────────────────────────────────────
    -- SALE: an ordinary invoice in full (step 4 proved it fits); an imported one up to what each
    -- bucket holds, lines in order -- 0051's rule, on the selection. REJECTION: in full, a record.
    with wanted as (
        select l.line_id, l.grade_id, l.size_id, l.price_per_ct,
               coalesce(l.selection_ct, 0) as selection_ct,
               coalesce(l.rejection_ct, 0) as rejection_ct
          from public.sales_line l
         where l.invoice_id = p_invoice_id
    ),
    capped as (
        select w.*,
               case when not v_imported then w.selection_ct
                    else greatest(0, least(w.selection_ct,
                             greatest(coalesce(p.balance_ct, 0), 0)
                             - coalesce(sum(w.selection_ct) over (partition by w.grade_id, w.size_id
                                                                  order by w.line_id
                                                                  rows between unbounded preceding and 1 preceding), 0)))
               end as sale_out
          from wanted w
          left join public.v_stock_position p
                 on p.grade_id = w.grade_id and p.size_id = w.size_id
    )
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by, reason)
    select p_invoice_date, c.grade_id, c.size_id, 'SALE', c.sale_out,
           c.price_per_ct, 'sales_line', c.line_id, auth.uid(), v_note
      from capped c where c.sale_out > 0
    union all
    select p_invoice_date, c.grade_id, c.size_id, 'REJECTION', c.rejection_ct,
           c.price_per_ct, 'sales_line', c.line_id, auth.uid(), v_note
      from capped c where c.rejection_ct > 0;

    -- ── 7 · the header, keeping the number and the status ──────────────────
    update public.sales_invoice
       set invoice_date = p_invoice_date,
           buyer_id     = p_buyer_id,
           broker_id    = p_broker_id,
           broker_pct   = p_broker_pct,
           terms_days   = p_terms_days,
           doc_type     = p_doc_type,
           currency_id  = p_currency_id,
           updated_by   = auth.uid()
     where invoice_id = p_invoice_id;

    return jsonb_build_object('ok', true, 'invoice_no', v_no, 'reason', v_reason);
end;
$$;

comment on function public.edit_posted_invoice is
    '0055. Corrects a POSTED invoice in one transaction: deletes its SALE/REJECTION movements (and any 0042 import_edit adjustment), replaces its lines and writes them again. Only the SALE moves stock: an ordinary invoice is refused when a bucket cannot cover its selection; an imported one is capped on its selection (0051). REJECTION is written in full as a record. A 0055 rejection hold on the invoice is left alone. Supersedes 0054.';


-- ── 5 · the sales import ────────────────────────────────────────────────────
-- 0051's body. The cap, the shortfall and the carats reported are all on the SELECTION; the
-- rejection is written in full as a record. Holds are not 'sales_line' rows, so the delete of the
-- previous import's movements leaves them where they are.
create or replace function public.replace_imported_sales(p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_old_ids     bigint[];
    v_deleted     integer := 0;
    v_invoices    integer := 0;
    v_lines       integer := 0;
    v_receipts    integer := 0;
    v_currency    bigint;
    v_stock_lines integer := 0;
    v_stock_ct    numeric := 0;
    v_short_ct    numeric := 0;
    v_short_bkts  integer := 0;
begin
    if p_payload is null or jsonb_typeof(p_payload->'invoices') <> 'array' then
        raise exception 'replace_imported_sales expects {"invoices": [...]}';
    end if;

    if jsonb_array_length(p_payload->'invoices') = 0 then
        raise exception 'replace_imported_sales was given no invoices';
    end if;

    v_currency := (p_payload->>'currency_id')::bigint;
    if v_currency is null then
        raise exception 'replace_imported_sales needs a currency_id';
    end if;

    select coalesce(array_agg(invoice_id), '{}')
      into v_old_ids
      from public.sales_invoice
     where invoice_no like 'MIG-%';

    if array_length(v_old_ids, 1) is not null then
        perform public.clear_import_edits(v_old_ids);

        delete from public.stock_movement
         where ref_type = 'sales_line'
           and ref_id in (select line_id from public.sales_line
                           where invoice_id = any(v_old_ids));

        delete from public.receipt     where invoice_id = any(v_old_ids);
        delete from public.sales_line  where invoice_id = any(v_old_ids);
        delete from public.sales_invoice where invoice_id = any(v_old_ids);
        get diagnostics v_deleted = row_count;
    end if;

    with incoming as (
        select inv from jsonb_array_elements(p_payload->'invoices') as inv
    ),
    written as (
        insert into public.sales_invoice
            (invoice_no, invoice_date, buyer_id, broker_id, broker_pct,
             terms_days, doc_type, currency_id, status,
             created_by, updated_by)
        select inv->>'invoice_no',
               (inv->>'invoice_date')::date,
               (inv->>'buyer_id')::bigint,
               nullif(inv->>'broker_id', '')::bigint,
               coalesce((inv->>'broker_pct')::numeric, 0),
               coalesce((inv->>'terms_days')::integer, 0),
               coalesce(inv->>'doc_type', 'BILL'),
               v_currency,
               'POSTED',
               auth.uid(),
               auth.uid()
          from incoming
        returning invoice_id, invoice_no
    )
    select count(*) into v_invoices from written;

    with incoming as (
        select inv->>'invoice_no' as no,
               jsonb_array_elements(coalesce(inv->'lines', '[]'::jsonb)) as ln
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct,
         price_per_ct, ex_rate, less1_pct, less2_pct, remark)
    select i.invoice_id,
           (c.ln->>'grade_id')::bigint,
           (c.ln->>'size_id')::bigint,
           (c.ln->>'gross_weight_ct')::numeric,
           (c.ln->>'selection_ct')::numeric,
           (c.ln->>'price_per_ct')::numeric,
           coalesce((c.ln->>'ex_rate')::numeric, 1),
           coalesce((c.ln->>'less1_pct')::numeric, 0),
           coalesce((c.ln->>'less2_pct')::numeric, 0),
           nullif(c.ln->>'remark', '')
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no;

    get diagnostics v_lines = row_count;

    with incoming as (
        select inv->>'invoice_no' as no,
               (inv->>'received')::numeric as received,
               (inv->>'invoice_date')::date as on_date
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.receipt (invoice_id, receipt_date, amount, method, created_by)
    select i.invoice_id, c.on_date, c.received, 'IMPORTED', auth.uid()
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no
     where c.received is not null and c.received > 0;

    get diagnostics v_receipts = row_count;

    -- 0055. The SELECTION is what leaves the bucket, so it is what is capped (0051's rule: oldest
    -- first, never below zero) and what is reported. The rejection is written in full as a record.
    with wanted as (
        select l.line_id, l.grade_id, l.size_id, l.price_per_ct,
               i.invoice_date,
               coalesce(l.selection_ct, 0) as selection_ct,
               coalesce(l.rejection_ct, 0) as rejection_ct
          from public.sales_line l
          join public.sales_invoice i on i.invoice_id = l.invoice_id
         where i.invoice_no like 'MIG-%'
    ),
    running as (
        select w.*,
               greatest(coalesce(p.balance_ct, 0), 0) as room,
               coalesce(sum(w.selection_ct) over (partition by w.grade_id, w.size_id
                                                  order by w.invoice_date, w.line_id
                                                  rows between unbounded preceding and 1 preceding), 0)
                   as taken_before
          from wanted w
          left join public.v_stock_position p
                 on p.grade_id = w.grade_id and p.size_id = w.size_id
    ),
    capped as (
        select r.*,
               greatest(0, least(r.selection_ct, r.room - r.taken_before)) as sale_out
          from running r
    ),
    wrote as (
        insert into public.stock_movement
            (movement_date, grade_id, size_id, movement_type, weight_ct,
             price_per_ct, ref_type, ref_id, created_by)
        select c.invoice_date, c.grade_id, c.size_id, 'SALE', c.sale_out,
               c.price_per_ct, 'sales_line', c.line_id, auth.uid()
          from capped c
         where c.sale_out > 0
        union all
        select c.invoice_date, c.grade_id, c.size_id, 'REJECTION', c.rejection_ct,
               c.price_per_ct, 'sales_line', c.line_id, auth.uid()
          from capped c
         where c.rejection_ct > 0
        returning movement_type, weight_ct
    ),
    took as (
        -- What actually LEFT stock: the SALE rows only.
        select coalesce(sum(weight_ct) filter (where movement_type = 'SALE'), 0) as ct_out from wrote
    ),
    counted as (
        select count(*) filter (where c.sale_out > 0) as lines_out from capped c
    ),
    fell_short as (
        select coalesce(round(sum(c.selection_ct - c.sale_out), 4), 0) as short_ct,
               count(distinct (c.grade_id, c.size_id))                as short_buckets
          from capped c
         where c.selection_ct - c.sale_out > 0.00005
    )
    select counted.lines_out, round(took.ct_out, 4),
           fell_short.short_ct, fell_short.short_buckets
      into v_stock_lines, v_stock_ct, v_short_ct, v_short_bkts
      from took, counted, fell_short;

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'invoices', v_invoices, 'lines', v_lines,
                              'receipts', v_receipts,
                              'stock_lines', v_stock_lines,
                              'stock_ct', v_stock_ct,
                              'short_ct', v_short_ct,
                              'short_buckets', v_short_bkts);
end;
$$;

comment on function public.replace_imported_sales is
    '0055. Replaces the imported MIG- sales history in one transaction. Each line writes a SALE for its selection, capped oldest-first at what the bucket holds and never below zero (0051), and a REJECTION for its rejection as a record that moves no stock. stock_ct / short_ct / short_buckets report the SELECTION. Never deletes 0055 rejection holds, so a re-import cannot add history back. Supersedes 0051.';


-- ── 6 · cancelling: give back what was taken, and nothing that was not ───────
-- 0010's body. SALE only (a 0055 rejection never left), plus the invoice's own hold if it has one,
-- because that hold IS this invoice's old rejection deduction -- releasing it is what cancelling did
-- before 0055, when the whole gross came back.
create or replace function public.cancel_invoice(p_invoice_id bigint, p_reason text default null)
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

    if p_reason is null or btrim(p_reason) = '' then
        raise exception 'Cancelling an invoice requires a reason';
    end if;

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
           and m.movement_type = 'SALE'                       -- 0055: the rejection never left
        union all
        select current_date, h.grade_id, h.size_id, 'ADJUST', -h.weight_ct,
               null, 'cancel', p_invoice_id, auth.uid(),
               'Reversal of invoice ' || p_invoice_id || ' (its pre-0055 rejection hold): ' || p_reason
          from public.stock_movement h
         where h.ref_type = 'rejection_hold'
           and h.ref_id = p_invoice_id;
    end if;

    update public.sales_invoice
       set status = 'CANCELLED', updated_by = auth.uid()
     where invoice_id = p_invoice_id;

    return jsonb_build_object('ok', true, 'reason', p_reason);
end;
$$;


-- ── 7 · a rejection recorded by hand: a record, not a withdrawal ────────────
-- The live body with the stock check removed: a rejection no longer takes carats out, so there is
-- nothing for the bucket to cover. Movement and dispositions are written exactly as before.
create or replace function public.record_rejection(
    p_grade_id     bigint,
    p_size_id      bigint,
    p_weight_ct    numeric,
    p_price_per_ct numeric default null,
    p_date         date    default current_date,
    p_client_ref   uuid    default null,
    p_dispositions jsonb   default '[]'::jsonb
)
returns jsonb
language plpgsql
as $$
declare
    v_movement_id bigint;
    v_kept        integer := 0;
begin
    if p_weight_ct is null or p_weight_ct <= 0 then
        raise exception 'Rejection weight must be greater than zero';
    end if;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, created_by, client_ref)
    values (p_date, p_grade_id, p_size_id, 'REJECTION', p_weight_ct,
            p_price_per_ct, 'manual', auth.uid(), p_client_ref)
    returning movement_id into v_movement_id;

    if jsonb_typeof(p_dispositions) = 'array' and jsonb_array_length(p_dispositions) > 0 then
        insert into public.rejection_disposition (movement_id, to_grade_id, weight_ct, note, created_by)
        select v_movement_id, nullif(d->>'to_grade_id', '')::bigint, (d->>'weight_ct')::numeric,
               nullif(d->>'note', ''), auth.uid()
          from jsonb_array_elements(p_dispositions) as d
         where (d->>'weight_ct')::numeric > 0;
        get diagnostics v_kept = row_count;
    end if;

    return jsonb_build_object('ok', true, 'warning', null, 'movement_id', v_movement_id,
                              'dispositions', v_kept);
end;
$$;


-- ── 8 · and prove the position did not move ─────────────────────────────────
do $$
declare
    v_bad integer;
    v_eg  text;
begin
    select count(*), min(format('grade %s x size %s: %s -> %s', b.grade_id, b.size_id, b.ledger_ct, a.ledger_ct))
      into v_bad, v_eg
      from m0055_before b
      join public.v_stock_position a on a.grade_id = b.grade_id and a.size_id = b.size_id
     where a.ledger_ct <> b.ledger_ct;

    if v_bad > 0 then
        raise exception '0055 would change % bucket(s), e.g. %. Nothing was applied.', v_bad, v_eg;
    end if;
end;
$$;

commit;


-- ---------------------------------------------------------------------------
-- Verification. Every row must read 'ok'.
-- ---------------------------------------------------------------------------
select 'a REJECTION no longer moves the balance' as check,
       case when pg_get_viewdef('public.v_stock_position'::regclass) ilike '%REJECTION%'
            then 'ok' else 'NOT APPLIED' end as status
union all
select 'every old rejection is held, carat for carat',
       case when (select coalesce(sum(weight_ct), 0) from public.stock_movement
                   where movement_type = 'REJECTION'
                     and created_at < (select min(created_at) from public.stock_movement
                                        where ref_type in ('rejection_hold', 'rej_hold_mvmt')))
               = -(select coalesce(sum(weight_ct), 0) from public.stock_movement
                    where ref_type in ('rejection_hold', 'rej_hold_mvmt'))
            then 'ok' else 'HOLDS DO NOT MATCH' end
union all
select 'posting needs the selection only',
       case when pg_get_functiondef('public.post_invoice'::regproc) like '%sum(selection_ct) as out_ct%'
            then 'ok' else 'NOT APPLIED' end
union all
select 'cancelling gives back SALE only, plus the invoice''s hold',
       case when pg_get_functiondef('public.cancel_invoice'::regproc) like '%rejection_hold%'
            then 'ok' else 'NOT APPLIED' end
union all
select 'a hand-recorded rejection no longer checks stock',
       case when pg_get_functiondef('public.record_rejection'::regproc) not like '%assert_stock%'
            then 'ok' else 'NOT APPLIED' end;
