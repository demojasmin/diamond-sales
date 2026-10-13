-- ---------------------------------------------------------------------------
-- Test for 0055 · only SOLD carats leave stock; a rejection is a record.
--
-- RUN ON DEMO ONLY (nzcvjaixgqoliyrotstz), AFTER 0055 is applied.
--
-- NOTHING IS SAVED. One DO block that ENDS BY RAISING AN ERROR on purpose, which rolls back
-- everything it did -- including the sales re-import in check 14, which inside this block replaces
-- Demo's imported invoices and is then undone with the rest. The results arrive as that error's
-- message, in red; that is the expected outcome. Every row must end ': ok'.
--
-- It works only in grade x size buckets that have never had a movement or a hold, so real stock is
-- never what it measures.
-- ---------------------------------------------------------------------------
do $$
declare
    r   text[] := '{}';
    b   record;
    bk  bigint[][] := '{}';      -- [n][1]=grade, [n][2]=size
    v_buyer bigint; v_cur bigint;
    inv1 bigint; inv2 bigint; inv3 bigint; inv4 bigint; inv5 bigint; mig bigint; ln bigint;
    v_err text; v_res jsonb; x numeric; y numeric;
    ref uuid := gen_random_uuid();

    -- the shelf (ledger) of bucket n, and what is available (net of holds by sales entries)
    function_dummy int;
begin
    perform set_config('request.jwt.claims',
        json_build_object('sub', (select id::text from auth.users order by created_at limit 1),
                          'role', 'authenticated')::text, true);

    if to_regclass('public.v_stock_position') is null
       or pg_get_viewdef('public.v_stock_position'::regclass) not ilike '%REJECTION%' then
        raise exception 'TEST 0055: apply 0055 first';
    end if;

    for b in
        select gs.grade_id, gs.size_id
          from public.grade_size gs
          join public.grade g       on g.grade_id = gs.grade_id and g.active
          join public.size_bucket s on s.size_id  = gs.size_id  and s.active
         where not exists (select 1 from public.stock_movement m
                            where m.grade_id = gs.grade_id and m.size_id = gs.size_id)
           and not exists (select 1 from public.stock_reservation x
                            where x.grade_id = gs.grade_id and x.size_id = gs.size_id)
         order by gs.grade_id, gs.size_id
         limit 6
    loop
        bk := bk || array[[b.grade_id, b.size_id]];
    end loop;

    select buyer_id into v_buyer from public.buyer where active order by buyer_id limit 1;
    select currency_id into v_cur from public.currency where code = 'INR';
    if coalesce(array_length(bk, 1), 0) < 6 or v_buyer is null or v_cur is null then
        raise exception 'TEST 0055 could not start: needs six untraded buckets, an active buyer and INR';
    end if;

    -- Stock: A = 10, C = 10, D = 3, E = 4, F = 5; B is empty.
    insert into public.stock_movement (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct, ref_type, reason)
    values (current_date, bk[1][1], bk[1][2], 'INTAKE', 10, 1000, 'verify', 'VERIFY-0055'),
           (current_date, bk[3][1], bk[3][2], 'INTAKE', 10, 1000, 'verify', 'VERIFY-0055'),
           (current_date, bk[4][1], bk[4][2], 'INTAKE',  3, 1000, 'verify', 'VERIFY-0055'),
           (current_date, bk[5][1], bk[5][2], 'INTAKE',  4, 1000, 'verify', 'VERIFY-0055'),
           (current_date, bk[6][1], bk[6][2], 'INTAKE',  5, 1000, 'verify', 'VERIFY-0055');

    -- ── 1 · a sale takes out the SELECTION only, and the rejection is still recorded ──
    insert into public.sales_invoice (invoice_date, buyer_id, currency_id, status)
    values (current_date, v_buyer, v_cur, 'DRAFT') returning invoice_id into inv1;
    insert into public.sales_line (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct)
    values (inv1, bk[1][1], bk[1][2], 8, 6, 100);
    perform public.post_invoice(inv1, false);
    select ledger_ct into x from public.v_stock_position where grade_id = bk[1][1] and size_id = bk[1][2];
    select coalesce(sum(m.weight_ct), 0) into y from public.stock_movement m join public.sales_line l on l.line_id = m.ref_id
     where m.ref_type = 'sales_line' and m.movement_type = 'REJECTION' and l.invoice_id = inv1;
    r := r || format('1  sale 8 ct gross / 6 sold: stock 10 -> 4, rejection 2 still recorded   : %s',
                     case when x = 4 and y = 2 then 'ok' else 'BROKEN - stock ' || x || ', rejection row ' || y end);

    -- ── 2 · the stock check is on the selection: 12 gross / 3 sold fits in 4 ──
    insert into public.sales_invoice (invoice_date, buyer_id, currency_id, status)
    values (current_date, v_buyer, v_cur, 'DRAFT') returning invoice_id into inv2;
    insert into public.sales_line (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct)
    values (inv2, bk[1][1], bk[1][2], 12, 3, 100);
    begin v_err := public.post_invoice(inv2, false) ->> 'ok'; exception when others then v_err := sqlerrm; end;
    select ledger_ct into x from public.v_stock_position where grade_id = bk[1][1] and size_id = bk[1][2];
    r := r || format('2  12 ct gross / 3 sold posts against 4 ct on the shelf (4 -> 1)        : %s',
                     case when v_err = 'true' and x = 1 then 'ok' else 'BROKEN - ' || v_err || ', stock ' || x end);

    -- ── 3 · and refuses when the SELECTION does not fit ──
    insert into public.sales_invoice (invoice_date, buyer_id, currency_id, status)
    values (current_date, v_buyer, v_cur, 'DRAFT') returning invoice_id into inv3;
    insert into public.sales_line (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct)
    values (inv3, bk[1][1], bk[1][2], 2, 2, 100);
    begin perform public.post_invoice(inv3, true); raise exception 'NOT REFUSED';
    exception when others then v_err := sqlerrm; end;
    select ledger_ct into x from public.v_stock_position where grade_id = bk[1][1] and size_id = bk[1][2];
    r := r || format('3  2 ct sold against 1 ct is refused, nothing moves                      : %s',
                     case when v_err like 'Posting would take stock negative%"needed_ct": 2%' and x = 1
                          then 'ok' else 'BROKEN - ' || v_err || ', stock ' || x end);

    -- ── 4 · a fully rejected parcel needs no stock and moves none ──
    insert into public.sales_invoice (invoice_date, buyer_id, currency_id, status)
    values (current_date, v_buyer, v_cur, 'DRAFT') returning invoice_id into inv4;
    insert into public.sales_line (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct)
    values (inv4, bk[2][1], bk[2][2], 5, 0, 100);
    begin v_err := public.post_invoice(inv4, false) ->> 'ok'; exception when others then v_err := sqlerrm; end;
    select ledger_ct into x from public.v_stock_position where grade_id = bk[2][1] and size_id = bk[2][2];
    r := r || format('4  5 ct all rejected posts on an empty bucket, which stays at 0          : %s',
                     case when v_err = 'true' and x = 0 then 'ok' else 'BROKEN - ' || v_err || ', stock ' || x end);

    -- ── 5 · cancelling gives back the SALE only -- no credit for a rejection never taken ──
    perform public.cancel_invoice(inv1, 'VERIFY-0055');
    select ledger_ct into x from public.v_stock_position where grade_id = bk[1][1] and size_id = bk[1][2];
    r := r || format('5  cancelling the 6-sold sale gives back 6, not 8 (1 -> 7)              : %s',
                     case when x = 7 then 'ok' else 'BROKEN - stock ' || x end);

    -- ── 6 · an update moves only the change in selection, and twice is still once ──
    perform public.edit_posted_invoice(inv2, current_date, v_buyer, null, 0, 0, 'BILL', v_cur,
        jsonb_build_array(jsonb_build_object('grade_id', bk[1][1], 'size_id', bk[1][2],
                          'gross_weight_ct', 12, 'selection_ct', 5, 'price_per_ct', 100)), 'VERIFY-0055');
    perform public.edit_posted_invoice(inv2, current_date, v_buyer, null, 0, 0, 'BILL', v_cur,
        jsonb_build_array(jsonb_build_object('grade_id', bk[1][1], 'size_id', bk[1][2],
                          'gross_weight_ct', 12, 'selection_ct', 5, 'price_per_ct', 100)), 'VERIFY-0055');
    select ledger_ct into x from public.v_stock_position where grade_id = bk[1][1] and size_id = bk[1][2];
    r := r || format('6  updating 3 sold -> 5 sold, saved twice: 7 -> 5, not 3                 : %s',
                     case when x = 5 then 'ok' else 'BROKEN - stock ' || x end);

    -- ── 7 · an update the shelf cannot cover is refused on the selection ──
    begin
        perform public.edit_posted_invoice(inv2, current_date, v_buyer, null, 0, 0, 'BILL', v_cur,
            jsonb_build_array(jsonb_build_object('grade_id', bk[1][1], 'size_id', bk[1][2],
                              'gross_weight_ct', 20, 'selection_ct', 20, 'price_per_ct', 100)), 'VERIFY-0055');
        raise exception 'NOT REFUSED';
    exception when others then v_err := sqlerrm; end;
    select ledger_ct into x from public.v_stock_position where grade_id = bk[1][1] and size_id = bk[1][2];
    r := r || format('7  updating to 20 sold is refused (required 20, available 10)           : %s',
                     case when v_err like 'Insufficient stock for % required 20 ct, available 10 ct.%' and x = 5
                          then 'ok' else 'BROKEN - ' || v_err || ', stock ' || x end);

    -- ── 8 · an invoice from BEFORE 0055: its hold survives updates and is released by cancelling ──
    -- C = 10; the old rule took 4 sold + 3 rejected, and 0055 restated the 3 as a hold.
    insert into public.sales_invoice (invoice_no, invoice_date, buyer_id, currency_id, status)
    values ('INV-VERIFY-0055', current_date, v_buyer, v_cur, 'POSTED') returning invoice_id into inv5;
    insert into public.sales_line (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct)
    values (inv5, bk[3][1], bk[3][2], 7, 4, 100) returning line_id into ln;
    insert into public.stock_movement (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct, ref_type, ref_id, reason)
    values (current_date, bk[3][1], bk[3][2], 'SALE', 4, 100, 'sales_line', ln, null),
           (current_date, bk[3][1], bk[3][2], 'REJECTION', 3, 100, 'sales_line', ln, null),
           (current_date, bk[3][1], bk[3][2], 'ADJUST', -3, null, 'rejection_hold', inv5, 'VERIFY-0055 hold');
    perform public.edit_posted_invoice(inv5, current_date, v_buyer, null, 0, 0, 'BILL', v_cur,
        jsonb_build_array(jsonb_build_object('grade_id', bk[3][1], 'size_id', bk[3][2],
                          'gross_weight_ct', 7, 'selection_ct', 4, 'price_per_ct', 100)), 'VERIFY-0055');
    select ledger_ct into x from public.v_stock_position where grade_id = bk[3][1] and size_id = bk[3][2];
    r := r || format('8  an old invoice updated unchanged keeps its hold: still 3 ct            : %s',
                     case when x = 3 then 'ok' else 'BROKEN - stock ' || x || ' (the hold was lost or doubled)' end);

    perform public.cancel_invoice(inv5, 'VERIFY-0055');
    v_res := public.cancel_invoice(inv5, 'VERIFY-0055');          -- a second press changes nothing
    select ledger_ct into x from public.v_stock_position where grade_id = bk[3][1] and size_id = bk[3][2];
    r := r || format('9  cancelling it returns sale + hold, all 10 ct, and a 2nd cancel nothing : %s',
                     case when x = 10 and (v_res ->> 'already_cancelled') = 'true' then 'ok'
                          else 'BROKEN - stock ' || x end);

    -- ── 10 · an imported invoice is capped on its selection ──
    -- D = 3; the import wrote SALE 2 + REJECTION 3 for a 5 gross / 2 sold line.
    insert into public.sales_invoice (invoice_no, invoice_date, buyer_id, currency_id, status)
    values ('MIG-VERIFY-0055', current_date, v_buyer, v_cur, 'POSTED') returning invoice_id into mig;
    insert into public.sales_line (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct)
    values (mig, bk[4][1], bk[4][2], 5, 2, 100) returning line_id into ln;
    insert into public.stock_movement (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct, ref_type, ref_id)
    values (current_date, bk[4][1], bk[4][2], 'SALE', 2, 100, 'sales_line', ln),
           (current_date, bk[4][1], bk[4][2], 'REJECTION', 3, 100, 'sales_line', ln);
    perform public.edit_posted_invoice(mig, current_date, v_buyer, null, 0, 0, 'BILL', v_cur,
        jsonb_build_array(jsonb_build_object('grade_id', bk[4][1], 'size_id', bk[4][2],
                          'gross_weight_ct', 5, 'selection_ct', 2, 'price_per_ct', 100)), 'VERIFY-0055');
    select ledger_ct into x from public.v_stock_position where grade_id = bk[4][1] and size_id = bk[4][2];
    perform public.edit_posted_invoice(mig, current_date, v_buyer, null, 0, 0, 'BILL', v_cur,
        jsonb_build_array(jsonb_build_object('grade_id', bk[4][1], 'size_id', bk[4][2],
                          'gross_weight_ct', 10, 'selection_ct', 10, 'price_per_ct', 100)), 'VERIFY-0055');
    select ledger_ct into y from public.v_stock_position where grade_id = bk[4][1] and size_id = bk[4][2];
    r := r || format('10 imported: unchanged update keeps 1 ct; 10 sold is capped at 3 -> 0   : %s',
                     case when x = 1 and y = 0 then 'ok' else 'BROKEN - ' || x || ' then ' || y end);

    -- ── 11 · a rejection recorded by hand moves no stock ──
    v_res := public.record_rejection(bk[1][1], bk[1][2], 1.5, null, current_date, null, '[]'::jsonb);
    select ledger_ct into x from public.v_stock_position where grade_id = bk[1][1] and size_id = bk[1][2];
    r := r || format('11 a hand-recorded 1.5 ct rejection is recorded and stock stays 5        : %s',
                     case when x = 5 and (v_res ->> 'movement_id') is not null then 'ok'
                          else 'BROKEN - stock ' || x end);

    -- ── 12 · conversions and adjustments are untouched ──
    perform public.convert_stock(bk[1][1], bk[1][2], bk[2][1], bk[2][2], 1, null, current_date, null);
    perform public.adjust_stock(bk[1][1], bk[1][2], 0.5, 'VERIFY-0055', current_date, null);
    select ledger_ct into x from public.v_stock_position where grade_id = bk[1][1] and size_id = bk[1][2];
    select ledger_ct into y from public.v_stock_position where grade_id = bk[2][1] and size_id = bk[2][2];
    r := r || format('12 convert 1 ct A->B and adjust A +0.5: A 5 -> 4.5, B 0 -> 1             : %s',
                     case when x = 4.5 and y = 1 then 'ok' else 'BROKEN - A ' || x || ', B ' || y end);

    -- ── 13 · a sales-entry hold still lowers what is available, and gives it back ──
    perform public.reserve_line(ref, 'verify-0055', bk[1][1], bk[1][2], 2);
    select balance_ct into x from public.v_stock_position where grade_id = bk[1][1] and size_id = bk[1][2];
    perform public.release_line(ref, 'verify-0055');
    select balance_ct into y from public.v_stock_position where grade_id = bk[1][1] and size_id = bk[1][2];
    r := r || format('13 holding 2 ct leaves 2.5 available; releasing it gives 4.5 back        : %s',
                     case when x = 2.5 and y = 4.5 then 'ok' else 'BROKEN - ' || x || ' then ' || y end);

    -- ── 14 · the sales import: capped and reported on the selection; a re-import keeps old holds ──
    -- F = 5 with a pre-0055 hold of 1 against an imported invoice: 4 on the shelf.
    insert into public.stock_movement (movement_date, grade_id, size_id, movement_type, weight_ct, ref_type, ref_id, reason)
    values (current_date, bk[6][1], bk[6][2], 'ADJUST', -1, 'rejection_hold', mig, 'VERIFY-0055 hold');
    v_res := public.replace_imported_sales(jsonb_build_object('currency_id', v_cur, 'invoices',
        jsonb_build_array(jsonb_build_object('invoice_no', 'MIG-VERIFY-0055B', 'invoice_date', current_date,
            'buyer_id', v_buyer, 'lines', jsonb_build_array(jsonb_build_object(
                'grade_id', bk[5][1], 'size_id', bk[5][2], 'gross_weight_ct', 6, 'selection_ct', 5,
                'price_per_ct', 100))))));
    select ledger_ct into x from public.v_stock_position where grade_id = bk[5][1] and size_id = bk[5][2];
    select ledger_ct into y from public.v_stock_position where grade_id = bk[6][1] and size_id = bk[6][2];
    r := r || format('14 import 6 gross / 5 sold into 4 ct: takes 4, short 1, rejection kept; hold survives : %s',
                     case when x = 0 and (v_res ->> 'stock_ct')::numeric = 4 and (v_res ->> 'short_ct')::numeric = 1
                           and exists (select 1 from public.stock_movement m join public.sales_line l on l.line_id = m.ref_id
                                        join public.sales_invoice i on i.invoice_id = l.invoice_id
                                       where i.invoice_no = 'MIG-VERIFY-0055B' and m.movement_type = 'REJECTION'
                                         and m.weight_ct = 1)
                           and y = 4
                          then 'ok' else 'BROKEN - E ' || x || ', F ' || y || ', ' || v_res::text end);

    raise exception E'TEST 0055 · ROLLED BACK, NOTHING WAS SAVED\n%', array_to_string(r, E'\n');
end;
$$;
