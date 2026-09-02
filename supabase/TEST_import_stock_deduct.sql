-- ---------------------------------------------------------------------------
-- Does the import deduct every sale, only once, without going below zero, and
-- report what it took and what it could not? A test that proves it and keeps
-- nothing.
--
-- SAFE ON A LIVE DATABASE. Everything happens inside one transaction that ends
-- in ROLLBACK, so the invoices it writes, the movements it writes, and the MIG-
-- history replace_imported_sales deletes on the way in are all undone.
--
-- Read that twice before running it anyway: the function DELETES every MIG-
-- invoice as its first act. The rollback is what puts them back. Do not edit
-- the final ROLLBACK into a COMMIT.
--
-- WHAT IT PROVES
--
--   1  a sale dated BEFORE the stock count deducts   (0048 said it should not)
--   2  a sale dated AFTER  the stock count deducts   (0048 agreed)
--   3  the bucket falls by the WHOLE parcel, sold and rejected alike
--   4  importing the SAME sheet again deducts once, not twice
--   5  the function reports the CARATS it took out, not just the line count
--   6  a line bigger than its bucket takes only what is there and says what it
--      could not take -- the bucket lands on zero, never below  (0051)
--
-- Two invoices identical but for their date, so if the date still mattered the
-- test would catch it.
-- ---------------------------------------------------------------------------

begin;

do $$
declare
    v_counted   date;
    v_buyer     bigint;
    v_currency  bigint;
    v_grade     bigint;
    v_size      bigint;
    v_before    date;
    v_after     date;
    v_result    jsonb;
    v_payload   jsonb;
    v_moves     integer;
    v_start_ct  numeric;
    v_reported  numeric;
    v_once_ct   numeric;
    v_twice_ct  numeric;
    v_over_ct   numeric;
begin
    -- ── what this database looks like ──────────────────────────────────────
    -- The count date is no longer part of the rule; it is read only so the two
    -- test invoices can be put either side of it and prove it is ignored.
    select coalesce(max(movement_date), current_date) into v_counted
      from public.stock_movement where ref_type = 'stock_import';

    select buyer_id into v_buyer from public.buyer where active order by buyer_id limit 1;
    select currency_id into v_currency from public.currency order by currency_id limit 1;

    select grade_id, size_id into v_grade, v_size
      from public.v_stock_position
     where balance_ct > 5
     order by balance_ct desc
     limit 1;

    if v_buyer is null or v_currency is null or v_grade is null then
        raise exception 'Need one active buyer, one currency and one bucket holding more than 5 ct. Found buyer=%, currency=%, grade=%.',
                        v_buyer, v_currency, v_grade;
    end if;

    v_before := v_counted - 1;
    v_after  := v_counted + 1;

    select round(balance_ct, 4) into v_start_ct
      from public.v_stock_position where grade_id = v_grade and size_id = v_size;

    raise notice 'Stock last counted %. Test invoices dated % and %. Bucket holds % ct.',
                 v_counted, v_before, v_after, v_start_ct;

    -- ── two invoices, identical but for the date ───────────────────────────
    -- 2.0000 gross, 1.5000 sold, so 0.5000 rejects: both movement types appear.
    v_payload := jsonb_build_object(
        'currency_id', v_currency,
        'invoices', jsonb_build_array(
            jsonb_build_object(
                'invoice_no', 'MIG-TEST-BEFORE', 'invoice_date', v_before,
                'buyer_id', v_buyer, 'terms_days', 0, 'doc_type', 'BILL',
                'lines', jsonb_build_array(jsonb_build_object(
                    'grade_id', v_grade, 'size_id', v_size,
                    'gross_weight_ct', 2.0, 'selection_ct', 1.5,
                    'price_per_ct', 1000))),
            jsonb_build_object(
                'invoice_no', 'MIG-TEST-AFTER', 'invoice_date', v_after,
                'buyer_id', v_buyer, 'terms_days', 0, 'doc_type', 'BILL',
                'lines', jsonb_build_array(jsonb_build_object(
                    'grade_id', v_grade, 'size_id', v_size,
                    'gross_weight_ct', 2.0, 'selection_ct', 1.5,
                    'price_per_ct', 1000)))));

    v_result := public.replace_imported_sales(v_payload);
    raise notice 'First import reported: %', v_result;

    -- ── 1 · the invoice dated BEFORE the count must deduct ─────────────────
    -- This is the assertion 0048 would fail. It is the whole point of 0049.
    select count(*) into v_moves
      from public.stock_movement m
      join public.sales_line l on l.line_id = m.ref_id and m.ref_type = 'sales_line'
      join public.sales_invoice i on i.invoice_id = l.invoice_id
     where i.invoice_no = 'MIG-TEST-BEFORE';

    if v_moves <> 2 then
        raise exception 'FAILED: the invoice dated BEFORE the count wrote % movement(s), expected 2. The date must no longer matter.', v_moves;
    end if;
    raise notice 'PASS 1: the invoice dated % deducted (SALE + REJECTION).', v_before;

    -- ── 2 · and so must the one dated AFTER it ─────────────────────────────
    select count(*) into v_moves
      from public.stock_movement m
      join public.sales_line l on l.line_id = m.ref_id and m.ref_type = 'sales_line'
      join public.sales_invoice i on i.invoice_id = l.invoice_id
     where i.invoice_no = 'MIG-TEST-AFTER';

    if v_moves <> 2 then
        raise exception 'FAILED: the invoice dated AFTER the count wrote % movement(s), expected 2.', v_moves;
    end if;
    raise notice 'PASS 2: the invoice dated % deducted (SALE + REJECTION).', v_after;

    -- ── 3 · the bucket fell by both whole parcels ──────────────────────────
    -- 2.0000 each: what is sold and what is rejected both leave the bucket they
    -- were counted in, exactly as a posted sale takes them.
    select round(balance_ct, 4) into v_once_ct
      from public.v_stock_position where grade_id = v_grade and size_id = v_size;

    if round(v_start_ct - v_once_ct, 4) <> 4.0000 then
        raise exception 'FAILED: the bucket fell by % ct, expected 4.0000 (two whole parcels).',
                        round(v_start_ct - v_once_ct, 4);
    end if;
    raise notice 'PASS 3: the bucket fell % -> % , 4.0000 ct for two parcels.', v_start_ct, v_once_ct;

    -- ── 4 · the same sheet again must deduct once, not twice ───────────────
    -- The guard is the delete at the top of the function: it takes out the
    -- movements the previous import wrote before the lines they hang off go.
    v_result := public.replace_imported_sales(v_payload);
    raise notice 'Second import of the SAME sheet reported: %', v_result;

    select round(balance_ct, 4) into v_twice_ct
      from public.v_stock_position where grade_id = v_grade and size_id = v_size;

    if v_twice_ct <> v_once_ct then
        raise exception 'FAILED: re-importing moved the bucket again, % -> %. The same sheet must deduct once.',
                        v_once_ct, v_twice_ct;
    end if;
    raise notice 'PASS 4: re-importing left the bucket at % -- deducted once, not twice.', v_twice_ct;

    -- ── 5 · and it reports the weight, not just the count ─────────────────
    -- 0050. Two lines of 2.0000 gross each: 1.5 sold + 0.5 rejected, twice over.
    v_reported := (v_result->>'stock_ct')::numeric;

    if v_reported is null then
        raise exception 'FAILED: the function reported no stock_ct at all. 0050 is not applied.';
    end if;

    if round(v_reported, 4) <> 4.0000 then
        raise exception 'FAILED: reported % ct taken out, expected 4.0000 -- the weight of the movements it wrote.',
                        round(v_reported, 4);
    end if;

    if (v_result->>'stock_lines')::integer <> 2 then
        raise exception 'FAILED: reported % line(s), expected 2.', v_result->>'stock_lines';
    end if;

    raise notice 'PASS 5: reported % line(s) and % ct -- the weight matches what the bucket lost.',
                 v_result->>'stock_lines', round(v_reported, 4);

    -- ── 6 · a line bigger than its bucket takes only what is there ─────────
    -- 0051. One invoice asking for 10 ct MORE than the bucket holds. It must
    -- empty the bucket to exactly zero, never below, and report the 10 ct it
    -- could not take. This is the assertion 0049 and 0050 both fail: they wrote
    -- the whole line and left the position negative.
    --
    -- Replaces the two invoices above, so the bucket is back at its starting
    -- weight when this one is measured against it.
    v_result := public.replace_imported_sales(jsonb_build_object(
        'currency_id', v_currency,
        'invoices', jsonb_build_array(
            jsonb_build_object(
                'invoice_no', 'MIG-TEST-TOOBIG', 'invoice_date', v_after,
                'buyer_id', v_buyer, 'terms_days', 0, 'doc_type', 'BILL',
                'lines', jsonb_build_array(jsonb_build_object(
                    'grade_id', v_grade, 'size_id', v_size,
                    'gross_weight_ct', v_start_ct + 10, 'selection_ct', v_start_ct + 10,
                    'price_per_ct', 1000))))));
    raise notice 'Import of a line 10 ct bigger than the bucket reported: %', v_result;

    select round(balance_ct, 4) into v_over_ct
      from public.v_stock_position where grade_id = v_grade and size_id = v_size;

    if v_over_ct <> 0 then
        raise exception 'FAILED: the bucket ended at % ct, expected exactly 0.0000. An import must empty it, not overdraw it.',
                        v_over_ct;
    end if;

    if round((v_result->>'short_ct')::numeric, 4) <> 10.0000 then
        raise exception 'FAILED: reported short_ct of %, expected 10.0000 -- the carats the shelf did not have.',
                        v_result->>'short_ct';
    end if;

    if (v_result->>'short_buckets')::integer <> 1 then
        raise exception 'FAILED: reported % short bucket(s), expected 1.', v_result->>'short_buckets';
    end if;

    if round((v_result->>'stock_ct')::numeric, 4) <> round(v_start_ct, 4) then
        raise exception 'FAILED: reported % ct taken out, expected % -- everything the bucket had and no more.',
                        v_result->>'stock_ct', round(v_start_ct, 4);
    end if;

    raise notice 'PASS 6: took % ct (all there was), left the bucket at 0, and reported % ct short in % bucket(s).',
                 round(v_start_ct, 4), v_result->>'short_ct', v_result->>'short_buckets';

    raise notice 'ALL SIX PASSED. Rolling back: nothing here is kept.';
end $$;

-- ── the report, read before the rollback takes it away ─────────────────────
select i.invoice_no,
       i.invoice_date,
       m.movement_type,
       m.weight_ct,
       m.movement_date
  from public.sales_invoice i
  join public.sales_line l on l.invoice_id = i.invoice_id
  left join public.stock_movement m
         on m.ref_id = l.line_id and m.ref_type = 'sales_line'
 where i.invoice_no in ('MIG-TEST-BEFORE', 'MIG-TEST-AFTER', 'MIG-TEST-TOOBIG')
 order by i.invoice_no, m.movement_type;

-- ---------------------------------------------------------------------------
-- NOT a commit. Every invoice, line, receipt and movement above is undone, and
-- the MIG- history the function deleted on the way in comes back with it.
-- ---------------------------------------------------------------------------
rollback;
