-- ---------------------------------------------------------------------------
-- Does posting a sale deduct from the right bucket, and only that one?
--
-- Proves two claims about post_invoice, against a live database:
--
--   1. Posting a sale takes the carats out of the EXACT grade x size sold, and
--      leaves every other bucket in the database untouched.
--   2. Selling more than a bucket holds is refused, and nothing is written.
--
-- Everything it creates is marked 'VERIFY-SALE' and removed at the end. It
-- posts a REAL invoice against REAL stock for a moment, so run it on Demo.
--
-- Read the result table at the bottom: every row must say 'ok'.
-- ---------------------------------------------------------------------------

create temporary table if not exists vsale (
    grade_id bigint, size_id bigint, grade_code text, size_code text,
    before_ct numeric, invoice_id bigint, line_id bigint,
    other_before jsonb
);
delete from vsale;

-- ── 1 · a bucket with enough stock to sell 1 ct out of ─────────────────────
insert into vsale (grade_id, size_id, grade_code, size_code, before_ct)
select p.grade_id, p.size_id, p.grade_code, p.size_code, p.balance_ct
  from public.v_stock_position p
 where p.balance_ct >= 2
 order by p.balance_ct desc
 limit 1;

-- Every OTHER bucket, captured now. This is what must not move.
update vsale set other_before = (
    select coalesce(jsonb_object_agg(p.grade_id || ':' || p.size_id, p.balance_ct), '{}'::jsonb)
      from public.v_stock_position p
     where not (p.grade_id = vsale.grade_id and p.size_id = vsale.size_id)
       and p.balance_ct <> 0);


-- ── 2 · a draft invoice selling 1.0000 ct out of it ────────────────────────
insert into public.sales_invoice (invoice_date, buyer_id, currency_id, status, doc_type)
select current_date,
       (select buyer_id from public.buyer where active order by buyer_id limit 1),
       (select currency_id from public.currency order by currency_id limit 1),
       'DRAFT', 'BILL'
 where exists (select 1 from vsale)
returning invoice_id
;

update vsale set invoice_id = (select max(invoice_id) from public.sales_invoice);

insert into public.sales_line
    (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct, remark)
select v.invoice_id, v.grade_id, v.size_id, 1.0000, 1.0000, 1000, 'VERIFY-SALE'
  from vsale v;

update vsale set line_id = (select max(line_id) from public.sales_line);


-- ── 3 · post it ────────────────────────────────────────────────────────────
do $$
declare v record;
begin
    select * into v from vsale;
    perform public.post_invoice(v.invoice_id, false);
end $$;


-- ── 4 · the two claims, and a third worth having ───────────────────────────
create temporary table if not exists vsale_out (n int, check_ text, status text);
delete from vsale_out;

-- CLAIM 1a · the bucket sold from is down by exactly 1 ct.
insert into vsale_out
select 1, 'the bucket sold from is down by exactly the quantity sold',
       case when (select p.balance_ct from public.v_stock_position p
                   join vsale v on v.grade_id = p.grade_id and v.size_id = p.size_id)
                 = (select before_ct - 1 from vsale)
            then 'ok'
            else 'WRONG: was ' || (select before_ct::text from vsale)
                 || ', now ' || coalesce((select p.balance_ct::text from public.v_stock_position p
                                           join vsale v on v.grade_id = p.grade_id and v.size_id = p.size_id), 'null') end;

-- CLAIM 1b · NOTHING else moved. The whole point: the join is on grade AND size,
-- so no other grade and no other sieve of the same grade may have shifted.
insert into vsale_out
select 2, 'every other bucket in the database is untouched',
       case when (select count(*)
                    from public.v_stock_position p, vsale v
                   where not (p.grade_id = v.grade_id and p.size_id = v.size_id)
                     and p.balance_ct <> 0
                     and p.balance_ct::text is distinct from
                         (v.other_before ->> (p.grade_id || ':' || p.size_id))) = 0
            then 'ok' else 'ANOTHER BUCKET MOVED' end;

-- CLAIM 1c · one SALE movement, on the right bucket, for the right weight.
insert into vsale_out
select 3, 'exactly one SALE movement, on the grade and size sold',
       case when (select count(*) from public.stock_movement m, vsale v
                   where m.ref_type = 'sales_line' and m.ref_id = v.line_id
                     and m.movement_type = 'SALE'
                     and m.grade_id = v.grade_id and m.size_id = v.size_id
                     and m.weight_ct = 1) = 1
            then 'ok' else 'NOT WRITTEN AS EXPECTED' end;

-- CLAIM 2 · overselling is refused, and writes nothing.
do $$
declare v record; v_inv bigint; v_before numeric; v_after numeric; v_blocked boolean;
begin
    select * into v from vsale;
    select balance_ct into v_before from public.v_stock_position
     where grade_id = v.grade_id and size_id = v.size_id;

    insert into public.sales_invoice (invoice_date, buyer_id, currency_id, status, doc_type)
    values (current_date,
            (select buyer_id from public.buyer where active order by buyer_id limit 1),
            (select currency_id from public.currency order by currency_id limit 1),
            'DRAFT', 'BILL')
    returning invoice_id into v_inv;

    -- Ten times what the bucket holds.
    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct, remark)
    values (v_inv, v.grade_id, v.size_id, v_before * 10, v_before * 10, 1000, 'VERIFY-SALE');

    begin
        perform public.post_invoice(v_inv, false);
        v_blocked := false;
    exception when others then v_blocked := true;
    end;

    select balance_ct into v_after from public.v_stock_position
     where grade_id = v.grade_id and size_id = v.size_id;

    insert into vsale_out values
        (4, 'selling more than the bucket holds is refused',
            case when v_blocked then 'ok' else 'WRONGLY ALLOWED' end),
        (5, 'and the refused sale wrote nothing',
            case when v_after = v_before then 'ok'
                 else 'STOCK MOVED: ' || v_before::text || ' -> ' || v_after::text end);

    delete from public.stock_movement where ref_type = 'sales_line'
       and ref_id in (select line_id from public.sales_line where invoice_id = v_inv);
    delete from public.sales_line where invoice_id = v_inv;
    delete from public.sales_invoice where invoice_id = v_inv;
end $$;

insert into vsale_out
select 6, 'no bucket anywhere is negative',
       case when (select count(*) from public.v_stock_position where balance_ct < 0) = 0
            then 'ok' else 'NEGATIVE BUCKETS PRESENT' end;


-- ── 5 · the answer, then put the stock back ────────────────────────────────
select check_ as check, status,
       (select grade_code || ' x ' || size_code || ', was ' || before_ct::text || ' ct'
          from vsale) as tested_on
  from vsale_out order by n;

delete from public.stock_movement where ref_type = 'sales_line'
   and ref_id in (select line_id from public.sales_line
                   where invoice_id in (select invoice_id from vsale));
delete from public.sales_line   where invoice_id in (select invoice_id from vsale);
delete from public.sales_invoice where invoice_id in (select invoice_id from vsale);

drop table if exists vsale_out;
drop table if exists vsale;
