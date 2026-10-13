-- ---------------------------------------------------------------------------
-- Rollback for 0055 · back to "the whole parcel leaves stock".
--
-- Run THIS file, then re-run these three migration files as they are (each only redefines its
-- own functions, and touches no data):
--     supabase/migrations/0051_import_never_drives_stock_negative.sql   (replace_imported_sales)
--     supabase/migrations/0054_sales_never_overdraw_and_imported_edit_keeps_its_stock.sql
--                                                                       (post_invoice, edit_posted_invoice)
--
-- What this file does, in one transaction:
--   * puts back 0044's v_stock_position, in which a REJECTION subtracts again;
--   * deletes the 0055 holds (rejection_hold / rej_hold_mvmt), because the REJECTION rows they
--     stood in for are counted again by that view;
--   * puts back cancel_invoice (reverses SALE and REJECTION) and record_rejection (checks stock).
--
-- BALANCES: identical for everything that existed before 0055. A REJECTION written AFTER 0055
-- had no hold and moved nothing; under the old rule it deducts again, so those buckets fall by
-- exactly that much. The check at the end proves nothing else moved, or rolls the lot back.
-- ---------------------------------------------------------------------------
begin;

create temporary table r0055_before on commit drop as
select grade_id, size_id, ledger_ct from public.v_stock_position;

-- REJECTIONs written after 0055 (no hold stands in for them): what the old rule will take out.
create temporary table r0055_new_rejections on commit drop as
select m.grade_id, m.size_id, sum(m.weight_ct) as ct
  from public.stock_movement m
 where m.movement_type = 'REJECTION'
   and m.created_at >= coalesce((select min(created_at) from public.stock_movement
                                  where ref_type in ('rejection_hold', 'rej_hold_mvmt')), 'infinity')
 group by 1, 2;

delete from public.stock_movement where ref_type in ('rejection_hold', 'rej_hold_mvmt');

-- 0044's view, verbatim.
create or replace view public.v_stock_position as
with movement as (
    select
        m.grade_id,
        m.size_id,
        case when m.movement_type in ('INTAKE', 'CONVERT_IN') then m.weight_ct
             when m.movement_type = 'ADJUST'                  then m.weight_ct
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
    'CALC-6/7. Balance, average cost and value per grade x size. balance_ct is what is AVAILABLE: movements less the carats a sales entry has spoken for (0043). ledger_ct is what is HELD: the movement ledger alone, which is what the Stock report prints so that a reservation never moves a ledger document (0044). reserved_ct is the difference between them.';

-- 0010's cancel_invoice, verbatim.
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

-- record_rejection as it stood before 0055 (read from Priya, 2026-10-09).
create or replace function public.record_rejection(p_grade_id bigint, p_size_id bigint, p_weight_ct numeric, p_price_per_ct numeric DEFAULT NULL::numeric, p_date date DEFAULT CURRENT_DATE, p_client_ref uuid DEFAULT NULL::uuid, p_dispositions jsonb DEFAULT '[]'::jsonb)
 returns jsonb
 language plpgsql
as $function$ declare v_warning text; v_movement_id bigint; v_kept integer := 0; begin if p_weight_ct is null or p_weight_ct <= 0 then raise exception 'Rejection weight must be greater than zero'; end if; v_warning := public.assert_stock(p_grade_id, p_size_id, p_weight_ct); insert into public.stock_movement (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct, ref_type, created_by, client_ref) values (p_date, p_grade_id, p_size_id, 'REJECTION', p_weight_ct, p_price_per_ct, 'manual', auth.uid(), p_client_ref) returning movement_id into v_movement_id; if jsonb_typeof(p_dispositions) = 'array' and jsonb_array_length(p_dispositions) > 0 then insert into public.rejection_disposition (movement_id, to_grade_id, weight_ct, note, created_by) select v_movement_id, nullif(d->>'to_grade_id', '')::bigint, (d->>'weight_ct')::numeric, nullif(d->>'note', ''), auth.uid() from jsonb_array_elements(p_dispositions) as d where (d->>'weight_ct')::numeric > 0; get diagnostics v_kept = row_count; end if; return jsonb_build_object('ok', true, 'warning', v_warning, 'movement_id', v_movement_id, 'dispositions', v_kept); end; $function$;

-- Nothing moved except what post-0055 rejections now take out under the old rule.
do $$
declare
    v_bad integer;
begin
    select count(*) into v_bad
      from r0055_before b
      join public.v_stock_position a on a.grade_id = b.grade_id and a.size_id = b.size_id
      left join r0055_new_rejections n on n.grade_id = b.grade_id and n.size_id = b.size_id
     where a.ledger_ct <> round(b.ledger_ct - coalesce(n.ct, 0), 4);

    if v_bad > 0 then
        raise exception 'Rollback of 0055 would move % bucket(s) unexpectedly. Nothing was changed.', v_bad;
    end if;
end;
$$;

commit;

-- Now re-run 0051 and 0054 (see the header).
