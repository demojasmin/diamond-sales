-- ---------------------------------------------------------------------------
-- Test for 0054 · a sale never overdraws its bucket, and "Update sale" on an
-- imported invoice keeps its stock.
--
-- RUN ON DEMO ONLY (nzcvjaixgqoliyrotstz).
--
-- NOTHING IS SAVED. The whole test is one DO block that ENDS BY RAISING AN ERROR
-- on purpose, and an error rolls back everything the block did -- the test
-- invoices, lines, movements and the policy change. The results arrive as that
-- error's message: the editor shows it in red, and that is the expected outcome.
-- Only id sequences move (a gap in invoice/line/movement ids); no invoice number
-- is consumed.
--
-- HOW TO READ IT
--   Before 0054 is applied: rows 1, 4, 6, 7 and 9 say BROKEN -- the old faults.
--   After  0054 is applied: every row says ok.
--
-- It works in two grade x size buckets that have never had a movement, so real
-- stock is never what it measures, and the policy is set to the most permissive
-- 'allow' so a refusal can only come from 0054.
-- ---------------------------------------------------------------------------
do $$
declare
    r       text[] := '{}';
    g1 bigint; s1 bigint; g2 bigint; s2 bigint;
    v_buyer bigint; v_cur bigint; v_inv bigint; v_mig bigint; v_line bigint;
    v_err   text;
    v_bal   numeric;
    v_sale  numeric;

    -- one line, as edit_posted_invoice takes it
    function_lines jsonb;
begin
    -- auth.uid() for the functions under test: the first user on this project.
    perform set_config('request.jwt.claims',
        json_build_object('sub', (select id::text from auth.users order by created_at limit 1),
                          'role', 'authenticated')::text, true);

    -- Two buckets nobody has ever traded, that the catalogue allows to be sold.
    select gs.grade_id, gs.size_id into g1, s1
      from public.grade_size gs
      join public.grade g       on g.grade_id = gs.grade_id and g.active
      join public.size_bucket s on s.size_id  = gs.size_id  and s.active
     where not exists (select 1 from public.stock_movement m
                        where m.grade_id = gs.grade_id and m.size_id = gs.size_id)
       and not exists (select 1 from public.stock_reservation x
                        where x.grade_id = gs.grade_id and x.size_id = gs.size_id)
     order by gs.grade_id, gs.size_id
     limit 1;

    select gs.grade_id, gs.size_id into g2, s2
      from public.grade_size gs
      join public.grade g       on g.grade_id = gs.grade_id and g.active
      join public.size_bucket s on s.size_id  = gs.size_id  and s.active
     where not exists (select 1 from public.stock_movement m
                        where m.grade_id = gs.grade_id and m.size_id = gs.size_id)
       and not exists (select 1 from public.stock_reservation x
                        where x.grade_id = gs.grade_id and x.size_id = gs.size_id)
       and (gs.grade_id, gs.size_id) <> (g1, s1)
     order by gs.grade_id, gs.size_id
     limit 1;

    select buyer_id into v_buyer from public.buyer where active order by buyer_id limit 1;
    select currency_id into v_cur from public.currency where code = 'INR';

    if g2 is null or v_buyer is null or v_cur is null then
        raise exception 'TEST 0054 could not start: needs two untraded buckets, an active buyer and INR';
    end if;

    -- The most permissive policy, so any refusal below is 0054's and nothing else's.
    insert into public.app_config (key, value) values ('negative_stock', 'allow')
    on conflict (key) do update set value = excluded.value;

    -- ── AN ORDINARY SALE ─────────────────────────────────────────────────────
    -- Bucket 1 holds 10 ct. A draft asks for 12.
    insert into public.stock_movement (movement_date, grade_id, size_id, movement_type, weight_ct,
                                       price_per_ct, ref_type, reason)
    values (current_date, g1, s1, 'INTAKE', 10, 1000, 'verify', 'VERIFY-0054');

    insert into public.sales_invoice (invoice_date, buyer_id, currency_id, status)
    values (current_date, v_buyer, v_cur, 'DRAFT') returning invoice_id into v_inv;
    insert into public.sales_line (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct)
    values (v_inv, g1, s1, 12, 10, 100);

    -- 1 · refused, even under 'allow' and with the override passed
    begin
        perform public.post_invoice(v_inv, true);
        raise exception 'NOT REFUSED';            -- rolls this sub-block back, so the rest stays clean
    exception when others then v_err := sqlerrm;
    end;
    r := r || format('1  a sale bigger than its bucket is refused (allow + override) : %s',
                     case when v_err like 'Posting would take stock negative%' then 'ok'
                          else 'BROKEN - ' || v_err end);

    -- 2 · and nothing moved
    select balance_ct into v_bal from public.v_stock_position where grade_id = g1 and size_id = s1;
    r := r || format('2  ...and the bucket still holds 10 ct, the memo still a memo   : %s',
                     case when v_bal = 10 and (select status from public.sales_invoice where invoice_id = v_inv) = 'DRAFT'
                          then 'ok' else 'BROKEN - balance ' || v_bal end);

    -- 3 · a sale that fits still posts: 8 ct gross, 6 selected
    update public.sales_line set gross_weight_ct = 8, selection_ct = 6 where invoice_id = v_inv;
    begin
        v_err := (public.post_invoice(v_inv, false) ->> 'ok');
    exception when others then v_err := sqlerrm;
    end;
    select balance_ct into v_bal from public.v_stock_position where grade_id = g1 and size_id = s1;
    r := r || format('3  a sale that fits posts, and takes its 8 ct out               : %s',
                     case when v_err = 'true' and v_bal = 2 then 'ok'
                          else 'BROKEN - ' || coalesce(v_err, '?') || ', balance ' || v_bal end);

    -- 4 · Update sale to 15 ct: the bucket (2 + the 8 given back = 10) cannot cover it
    function_lines := jsonb_build_array(jsonb_build_object('grade_id', g1, 'size_id', s1,
                        'gross_weight_ct', 15, 'selection_ct', 15, 'price_per_ct', 100));
    begin
        perform public.edit_posted_invoice(v_inv, current_date, v_buyer, null, 0, 0, 'BILL', v_cur,
                                           function_lines, 'VERIFY-0054');
        raise exception 'NOT REFUSED';
    exception when others then v_err := sqlerrm;
    end;
    select balance_ct into v_bal from public.v_stock_position where grade_id = g1 and size_id = s1;
    r := r || format('4  an update the bucket cannot cover is refused, both figures named : %s',
                     case when v_err like 'Insufficient stock for % required 15 ct, available 10 ct.%' and v_bal = 2
                          then 'ok' else 'BROKEN - ' || v_err || ', balance ' || v_bal end);

    -- 5 · Update sale to 9 ct: fits
    function_lines := jsonb_build_array(jsonb_build_object('grade_id', g1, 'size_id', s1,
                        'gross_weight_ct', 9, 'selection_ct', 9, 'price_per_ct', 100));
    begin
        perform public.edit_posted_invoice(v_inv, current_date, v_buyer, null, 0, 0, 'BILL', v_cur,
                                           function_lines, 'VERIFY-0054');
        v_err := null;
    exception when others then v_err := sqlerrm;
    end;
    select balance_ct into v_bal from public.v_stock_position where grade_id = g1 and size_id = s1;
    r := r || format('5  an update that fits goes through: 10 - 9 = 1 ct left           : %s',
                     case when v_err is null and v_bal = 1 then 'ok'
                          else 'BROKEN - ' || coalesce(v_err, '') || ' balance ' || v_bal end);

    -- ── AN IMPORTED SALE ─────────────────────────────────────────────────────
    -- Bucket 2 holds 3 ct. An imported invoice sold 5, and the import (0051) could take only 3.
    insert into public.stock_movement (movement_date, grade_id, size_id, movement_type, weight_ct,
                                       price_per_ct, ref_type, reason)
    values (current_date, g2, s2, 'INTAKE', 3, 1000, 'verify', 'VERIFY-0054');

    insert into public.sales_invoice (invoice_no, invoice_date, buyer_id, currency_id, status)
    values ('MIG-VERIFY-0054', current_date, v_buyer, v_cur, 'POSTED') returning invoice_id into v_mig;
    insert into public.sales_line (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct)
    values (v_mig, g2, s2, 5, 5, 100) returning line_id into v_line;
    insert into public.stock_movement (movement_date, grade_id, size_id, movement_type, weight_ct,
                                       price_per_ct, ref_type, ref_id)
    values (current_date, g2, s2, 'SALE', 3, 100, 'sales_line', v_line);

    -- 6 · Update sale with NOTHING changed must leave the stock where it is (0 ct)
    function_lines := jsonb_build_array(jsonb_build_object('grade_id', g2, 'size_id', s2,
                        'gross_weight_ct', 5, 'selection_ct', 5, 'price_per_ct', 100));
    begin
        perform public.edit_posted_invoice(v_mig, current_date, v_buyer, null, 0, 0, 'BILL', v_cur,
                                           function_lines, 'VERIFY-0054');
        v_err := null;
    exception when others then v_err := sqlerrm;
    end;
    select balance_ct into v_bal from public.v_stock_position where grade_id = g2 and size_id = s2;
    r := r || format('6  updating an imported sale unchanged keeps its stock out (0 ct)   : %s',
                     case when v_err is null and v_bal = 0 then 'ok'
                          else 'BROKEN - ' || coalesce(v_err, '') || ' balance ' || v_bal
                               || ' (the update gave the carats back)' end);

    -- 7 · Update to 2 ct: 3 - 2 = 1 ct left
    function_lines := jsonb_build_array(jsonb_build_object('grade_id', g2, 'size_id', s2,
                        'gross_weight_ct', 2, 'selection_ct', 2, 'price_per_ct', 100));
    begin
        perform public.edit_posted_invoice(v_mig, current_date, v_buyer, null, 0, 0, 'BILL', v_cur,
                                           function_lines, 'VERIFY-0054');
        v_err := null;
    exception when others then v_err := sqlerrm;
    end;
    select balance_ct into v_bal from public.v_stock_position where grade_id = g2 and size_id = s2;
    select coalesce(sum(m.weight_ct), 0) into v_sale
      from public.stock_movement m join public.sales_line l on l.line_id = m.ref_id
     where m.ref_type = 'sales_line' and m.movement_type = 'SALE' and l.invoice_id = v_mig;
    r := r || format('7  reducing it to 2 ct leaves 1 ct, as a SALE of 2               : %s',
                     case when v_err is null and v_bal = 1 and v_sale = 2 then 'ok'
                          else 'BROKEN - ' || coalesce(v_err, '') || ' balance ' || v_bal || ', SALE ' || v_sale end);

    -- 8 · Raising it to 10 ct is not refused but capped at the 3 ct the bucket holds (0051's rule)
    function_lines := jsonb_build_array(jsonb_build_object('grade_id', g2, 'size_id', s2,
                        'gross_weight_ct', 10, 'selection_ct', 10, 'price_per_ct', 100));
    begin
        perform public.edit_posted_invoice(v_mig, current_date, v_buyer, null, 0, 0, 'BILL', v_cur,
                                           function_lines, 'VERIFY-0054');
        v_err := null;
    exception when others then v_err := sqlerrm;
    end;
    select balance_ct into v_bal from public.v_stock_position where grade_id = g2 and size_id = s2;
    r := r || format('8  raising it past the shelf is capped, never negative (0 ct)     : %s',
                     case when v_err is null and v_bal = 0 then 'ok'
                          else 'BROKEN - ' || coalesce(v_err, '') || ' balance ' || v_bal end);

    -- 9 · An adjustment an update under 0042 left behind is cleared, not counted twice
    insert into public.stock_movement (movement_date, grade_id, size_id, movement_type, weight_ct,
                                       ref_type, ref_id, reason)
    values (current_date, g2, s2, 'ADJUST', 4, 'import_edit', v_mig, 'VERIFY-0054 residue of a 0042 update');
    function_lines := jsonb_build_array(jsonb_build_object('grade_id', g2, 'size_id', s2,
                        'gross_weight_ct', 2, 'selection_ct', 2, 'price_per_ct', 100));
    begin
        perform public.edit_posted_invoice(v_mig, current_date, v_buyer, null, 0, 0, 'BILL', v_cur,
                                           function_lines, 'VERIFY-0054');
        v_err := null;
    exception when others then v_err := sqlerrm;
    end;
    select balance_ct into v_bal from public.v_stock_position where grade_id = g2 and size_id = s2;
    r := r || format('9  an old import_edit adjustment is cleared by the update (1 ct)  : %s',
                     case when v_err is null and v_bal = 1
                           and not exists (select 1 from public.stock_movement
                                            where ref_type = 'import_edit' and ref_id = v_mig)
                          then 'ok' else 'BROKEN - ' || coalesce(v_err, '') || ' balance ' || v_bal end);

    -- ── and undo every one of the above ─────────────────────────────────────
    raise exception E'TEST 0054 · ROLLED BACK, NOTHING WAS SAVED\n%', array_to_string(r, E'\n');
end;
$$;
