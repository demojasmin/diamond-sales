-- ---------------------------------------------------------------------------
-- Regression test for 0040 · correcting a POSTED invoice.
--
-- Runs against a live database because the rule is a plpgsql transaction and there is nowhere
-- else to exercise one. It builds its own scenario in a bucket that has never traded, checks
-- eight outcomes, and deletes exactly what it created.
--
-- SAFE ON A DATABASE WITH REAL STOCK. Everything it writes is marked 'VERIFY-0040' -- the
-- movements by reason, the invoice by its buyer note -- and the cleanup matches on those alone,
-- so it can neither touch nor remove a row anybody else wrote. Run it on Demo first.
--
-- Every row of the result must read 'ok'.
-- ---------------------------------------------------------------------------

create temporary table if not exists v0040 (
    grade_id bigint, size_id bigint, invoice_id bigint, invoice_no text, before_ct numeric
);
delete from v0040;

create temporary table if not exists v0040_out (n int, check_ text, status text);
delete from v0040_out;

-- ── 0 · a bucket nobody has traded, and 100 ct put into it ─────────────────
insert into v0040 (grade_id, size_id)
select g.grade_id, s.size_id
  from public.grade g
  cross join public.size_bucket s
 where g.active and s.active
   and not exists (select 1 from public.stock_movement m
                    where m.grade_id = g.grade_id and m.size_id = s.size_id)
 order by g.sort_order, s.sort_order
 limit 1;

delete from public.stock_movement where reason like 'VERIFY-0040%';

insert into public.stock_movement
    (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct, ref_type, reason)
select current_date, grade_id, size_id, 'INTAKE', 100, 1000, 'verify', 'VERIFY-0040'
  from v0040;


-- ── 1 · a real invoice, posted the ordinary way, selling 10 ct ─────────────
do $$
declare v record; v_inv bigint; v_res jsonb;
begin
    select * into v from v0040;

    insert into public.sales_invoice (invoice_date, buyer_id, currency_id, status, doc_type, terms_days)
    values (current_date,
            (select buyer_id from public.buyer where active order by buyer_id limit 1),
            (select currency_id from public.currency order by currency_id limit 1),
            'DRAFT', 'BILL', 30)
    returning invoice_id into v_inv;

    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct, remark)
    values (v_inv, v.grade_id, v.size_id, 10, 10, 1000, 'VERIFY-0040');

    v_res := public.post_invoice(v_inv, false);

    update v0040
       set invoice_id = v_inv,
           invoice_no = (select invoice_no from public.sales_invoice where invoice_id = v_inv),
           before_ct  = (select balance_ct from public.v_stock_position
                          where grade_id = v.grade_id and size_id = v.size_id);
end $$;

insert into v0040_out
select 1, 'the sale posted and took 10 ct out',
       case when (select before_ct from v0040) = 90 then 'ok'
            else 'WRONG: ' || coalesce((select before_ct::text from v0040), 'null') end;


-- ── 2 · correct it from 10 ct to 25 ct ─────────────────────────────────────
do $$
declare v record; v_res jsonb; v_after numeric;
begin
    select * into v from v0040;

    v_res := public.edit_posted_invoice(
        v.invoice_id, current_date,
        (select buyer_id from public.buyer where active order by buyer_id limit 1),
        null, 0, 30, 'BILL',
        (select currency_id from public.currency order by currency_id limit 1),
        jsonb_build_array(jsonb_build_object(
            'grade_id', v.grade_id, 'size_id', v.size_id,
            'gross_weight_ct', 25, 'selection_ct', 25, 'price_per_ct', 1000,
            'ex_rate', 1, 'less1_pct', 0, 'less2_pct', 0, 'remark', 'VERIFY-0040')),
        'VERIFY-0040 corrected weight');

    select balance_ct into v_after from public.v_stock_position
     where grade_id = v.grade_id and size_id = v.size_id;

    insert into v0040_out values
        (2, 'the correction is accepted', case when v_res->>'ok' = 'true' then 'ok' else 'REFUSED' end),
        (3, 'stock now reflects 25 ct out, not 10',
            case when v_after = 75 then 'ok' else 'WRONG: ' || coalesce(v_after::text, 'null') end),
        (4, 'the invoice keeps its number and stays POSTED',
            case when (select invoice_no || '/' || status from public.sales_invoice
                        where invoice_id = v.invoice_id) = v.invoice_no || '/POSTED'
                 then 'ok'
                 else (select invoice_no || '/' || status from public.sales_invoice
                        where invoice_id = v.invoice_id) end);
end $$;

-- The old SALE movement must be GONE, not sitting beside a reversal: v_reconciliation compares
-- SALE against posted lines and does not net ADJUST, so a leftover would never balance again.
insert into v0040_out
-- Compared as NUMBERS. The first version of this matched sum(weight_ct)::text against '25' and
-- reported a failure reading "1/25.0000" -- the right answer, rendered at numeric's scale of 4.
-- A check that fails on how a value prints teaches whoever runs it to distrust the checks.
select 5, 'exactly one SALE movement remains, for 25 ct',
       case when v.n = 1 and v.ct = 25 then 'ok'
            else coalesce(v.n::text, '0') || ' movement(s), '
                 || coalesce(trim_scale(v.ct)::text, '0') || ' ct' end
  from (select count(*) as n, coalesce(sum(m.weight_ct), 0) as ct
          from public.stock_movement m
          join public.sales_line l on l.line_id = m.ref_id
         where m.ref_type = 'sales_line' and m.movement_type = 'SALE'
           and l.invoice_id = (select invoice_id from v0040)) v;

insert into v0040_out
select 6, 'the bucket reconciles: movements match the invoice',
       case when (select reconciles from public.v_reconciliation r
                   join v0040 v on true
                   join public.grade g on g.grade_id = v.grade_id
                   join public.size_bucket s on s.size_id = v.size_id
                  where r.grade_code = g.code and r.size_code = s.code)
            then 'ok' else 'DOES NOT RECONCILE' end;


-- ── 3 · a correction that cannot be honoured changes nothing ───────────────
do $$
declare v record; v_before numeric; v_after numeric; v_blocked boolean;
begin
    select * into v from v0040;
    select balance_ct into v_before from public.v_stock_position
     where grade_id = v.grade_id and size_id = v.size_id;

    begin
        perform public.edit_posted_invoice(
            v.invoice_id, current_date,
            (select buyer_id from public.buyer where active order by buyer_id limit 1),
            null, 0, 30, 'BILL',
            (select currency_id from public.currency order by currency_id limit 1),
            jsonb_build_array(jsonb_build_object(
                'grade_id', v.grade_id, 'size_id', v.size_id,
                'gross_weight_ct', 5000, 'selection_ct', 5000, 'price_per_ct', 1000,
                'ex_rate', 1, 'less1_pct', 0, 'less2_pct', 0, 'remark', 'VERIFY-0040')),
            'VERIFY-0040 far too much');
        v_blocked := false;
    exception when others then v_blocked := true;
    end;

    select balance_ct into v_after from public.v_stock_position
     where grade_id = v.grade_id and size_id = v.size_id;

    insert into v0040_out values
        (7, 'a correction that would go negative is refused',
            case when v_blocked then 'ok' else 'WRONGLY ALLOWED' end),
        (8, 'and it ROLLED BACK: the stock and the lines are untouched',
            case when v_after = v_before
                  and (select sum(selection_ct) from public.sales_line
                        where invoice_id = v.invoice_id) = 25
                 then 'ok'
                 else 'stock ' || v_before::text || ' -> ' || coalesce(v_after::text, 'null') end);
end $$;


-- ── 4 · a reason is required, and imported sales are refused ───────────────
do $$
declare v record; v_ok boolean;
begin
    select * into v from v0040;

    begin
        perform public.edit_posted_invoice(
            v.invoice_id, current_date,
            (select buyer_id from public.buyer where active order by buyer_id limit 1),
            null, 0, 30, 'BILL',
            (select currency_id from public.currency order by currency_id limit 1),
            jsonb_build_array(jsonb_build_object(
                'grade_id', v.grade_id, 'size_id', v.size_id,
                'gross_weight_ct', 20, 'selection_ct', 20, 'price_per_ct', 1000)),
            '   ');
        v_ok := false;
    exception when others then v_ok := true;
    end;
    insert into v0040_out values (9, 'an unexplained correction is refused',
                                  case when v_ok then 'ok' else 'WRONGLY ALLOWED' end);

    -- An imported invoice, if this database holds one.
    if exists (select 1 from public.sales_invoice where invoice_no like 'MIG-%' and status = 'POSTED') then
        begin
            perform public.edit_posted_invoice(
                (select invoice_id from public.sales_invoice
                  where invoice_no like 'MIG-%' and status = 'POSTED' order by invoice_id limit 1),
                current_date,
                (select buyer_id from public.buyer where active order by buyer_id limit 1),
                null, 0, 30, 'BILL',
                (select currency_id from public.currency order by currency_id limit 1),
                jsonb_build_array(jsonb_build_object(
                    'grade_id', v.grade_id, 'size_id', v.size_id,
                    'gross_weight_ct', 1, 'selection_ct', 1, 'price_per_ct', 1000)),
                'VERIFY-0040 should never apply');
            v_ok := false;
        exception when others then v_ok := true;
        end;
        insert into v0040_out values (10, 'an IMPORTED sale is refused',
                                      case when v_ok then 'ok' else 'WRONGLY ALLOWED' end);
    else
        insert into v0040_out values (10, 'an IMPORTED sale is refused',
                                      'skipped - no MIG- invoice in this database');
    end if;
end $$;

insert into v0040_out
select 11, 'no bucket anywhere is negative',
       case when (select count(*) from public.v_stock_position where balance_ct < 0) = 0
            then 'ok' else 'NEGATIVE BUCKETS PRESENT' end;


-- ── 5 · the answer, then remove everything this script created ─────────────
select check_ as check, status from v0040_out order by n;

delete from public.stock_movement
 where ref_type = 'sales_line'
   and ref_id in (select line_id from public.sales_line
                   where invoice_id in (select invoice_id from v0040));
delete from public.sales_line   where invoice_id in (select invoice_id from v0040);
delete from public.sales_invoice where invoice_id in (select invoice_id from v0040);
delete from public.stock_movement where reason like 'VERIFY-0040%';

drop table if exists v0040_out;
drop table if exists v0040;
