-- ---------------------------------------------------------------------------
-- Option A of REJECTION_CORRECTION_PLAN.md · give back every rejection deducted before 0055.
--
-- RUN ONLY AFTER: 0055 is applied, a fresh backup is taken, and the release is approved.
--
-- Deletes the 0055 holds (ref_type rejection_hold / rej_hold_mvmt) in one transaction. The
-- rejection records themselves are NOT touched, and the audit trail keeps a DELETE row with the
-- full old values of every hold removed, so this can be redone or undone from the audit log.
--
-- Checks before committing: the shelf rises by exactly the carats the holds held, in every bucket,
-- and nothing else moves. Otherwise it rolls back and changes nothing.
-- ---------------------------------------------------------------------------
begin;

create temporary table rel_before on commit drop as
select grade_id, size_id, ledger_ct from public.v_stock_position;

create temporary table rel_holds on commit drop as
select grade_id, size_id, -sum(weight_ct) as held
  from public.stock_movement
 where ref_type in ('rejection_hold', 'rej_hold_mvmt')
 group by 1, 2;

do $$
begin
    if pg_get_viewdef('public.v_stock_position'::regclass) not ilike '%REJECTION%' then
        raise exception 'Apply 0055 first. Nothing was changed.';
    end if;
    if not exists (select 1 from rel_holds) then
        raise exception 'There are no rejection holds to release. Nothing was changed.';
    end if;
end;
$$;

delete from public.stock_movement where ref_type in ('rejection_hold', 'rej_hold_mvmt');

do $$
declare
    v_bad integer;
begin
    select count(*) into v_bad
      from rel_before b
      join public.v_stock_position a on a.grade_id = b.grade_id and a.size_id = b.size_id
      left join rel_holds h on h.grade_id = b.grade_id and h.size_id = b.size_id
     where a.ledger_ct <> round(b.ledger_ct + coalesce(h.held, 0), 4);

    if v_bad > 0 then
        raise exception 'Release would move % bucket(s) by something other than their hold. Nothing was changed.', v_bad;
    end if;
end;
$$;

-- What was given back, for the record (shown by the editor as the result).
select round(sum(held), 4)                                as released_ct,
       count(*)                                           as buckets,
       (select round(sum(ledger_ct), 4) from public.v_stock_position) as shelf_now
  from rel_holds;

commit;
