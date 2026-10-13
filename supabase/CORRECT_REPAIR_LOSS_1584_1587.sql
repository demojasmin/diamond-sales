-- ---------------------------------------------------------------------------
-- Correct four repair losses that were entered as stock INCREASES (Priya, 12 Oct 2026).
--
--   1584  1MB  x +6.5   +0.03 ct  "Repairing Loss"
--   1585  1MB  x +11    +0.07 ct  "Reparing Loss"
--   1586  II   x +11    +0.07 ct  "Repair Loss"
--   1587  II   x 1/6    +0.02 ct  "Reparing Loss"
--
-- The client confirmed (13 Oct 2026) all four were real weight losses. Entered when the Adjust box
-- could not take a minus sign, they went in as +w instead of -w.
--
-- APPEND-ONLY, as the ledger requires: the four originals are not touched. Each gets two new
-- adjustments on the same bucket and date --
--     -w  reversing the mistaken increase
--     -w  the repair loss itself
-- so each bucket ends 2 x w lower: 0.38 ct across the four. A removal carries no cost, so the
-- average cost does not move (the small dilution the free +0.19 ct caused stays, as agreed).
--
-- One transaction. Refuses, changing nothing, unless every target is still exactly what it was
-- (a positive manual ADJUST with no rate whose reason names a loss) and has not been corrected
-- already. Safe to run twice: the second run refuses.
-- ---------------------------------------------------------------------------
begin;

-- THE TARGETS. The only line that differs between Priya and the Demo rehearsal.
create temporary table fix_targets on commit drop as
select movement_id from public.stock_movement where movement_id in (1584, 1585, 1586, 1587);

create temporary table fix_before on commit drop as
select p.grade_id, p.size_id, p.grade_code, p.size_code, p.ledger_ct, p.balance_ct, p.avg_cost, p.stock_value
  from public.v_stock_position p
 where (p.grade_id, p.size_id) in (select m.grade_id, m.size_id from public.stock_movement m
                                    where m.movement_id in (select movement_id from fix_targets));

do $$
declare
    v_bad text;
begin
    if (select count(*) from fix_targets) <> 4 then
        raise exception 'Expected 4 entries, found %. Nothing was changed.', (select count(*) from fix_targets);
    end if;

    select string_agg(m.movement_id::text, ', ') into v_bad
      from public.stock_movement m
     where m.movement_id in (select movement_id from fix_targets)
       and not (m.movement_type = 'ADJUST' and m.ref_type = 'manual' and m.weight_ct > 0
                and m.price_per_ct is null and m.reason ilike '%loss%');
    if v_bad is not null then
        raise exception 'Entries % are no longer the mistaken increases this corrects. Nothing was changed.', v_bad;
    end if;

    select string_agg(m.movement_id::text, ', ') into v_bad
      from public.stock_movement m
     where m.movement_id in (select movement_id from fix_targets)
       and exists (select 1 from public.stock_movement c
                    where c.reason like 'Correction of entry ' || m.movement_id || ':%');
    if v_bad is not null then
        raise exception 'Entries % are already corrected. Nothing was changed.', v_bad;
    end if;

    select string_agg(b.grade_code || ' x ' || b.size_code, ', ') into v_bad
      from fix_before b
     where b.balance_ct < (select 2 * sum(m.weight_ct) from public.stock_movement m
                            where m.movement_id in (select movement_id from fix_targets)
                              and m.grade_id = b.grade_id and m.size_id = b.size_id);
    if v_bad is not null then
        raise exception 'Not enough available stock in % to take the losses out. Nothing was changed.', v_bad;
    end if;
end;
$$;

insert into public.stock_movement (movement_date, grade_id, size_id, movement_type, weight_ct, ref_type, reason)
select m.movement_date, m.grade_id, m.size_id, 'ADJUST', -m.weight_ct, 'manual', x.reason
  from public.stock_movement m
 cross join lateral (values
     ('Correction of entry ' || m.movement_id || ': reverses +' || m.weight_ct
      || ' ct entered as an increase by mistake. Client confirmed a repair loss, 13 Oct 2026.'),
     ('Repair loss ' || m.weight_ct || ' ct, as entry ' || m.movement_id
      || ' should have recorded. Client confirmed, 13 Oct 2026.')) as x(reason)
 where m.movement_id in (select movement_id from fix_targets);

-- Proven before it commits: each bucket down by exactly 2 x w, its average cost unmoved.
do $$
begin
    if exists (select 1 from fix_before b
                 join public.v_stock_position p using (grade_id, size_id)
                 join (select grade_id, size_id, sum(weight_ct) w from public.stock_movement
                        where movement_id in (select movement_id from fix_targets) group by 1, 2) t
                using (grade_id, size_id)
                where b.ledger_ct - p.ledger_ct <> 2 * t.w or p.avg_cost <> b.avg_cost) then
        raise exception 'A bucket did not come out as expected. Nothing was changed.';
    end if;
end;
$$;

-- Before and after, per bucket. Every row says ok (anything else has already rolled back).
select b.grade_code, b.size_code,
       b.ledger_ct                                                        as ledger_before,
       p.ledger_ct                                                        as ledger_after,
       b.ledger_ct - p.ledger_ct                                          as taken_out,
       round(b.avg_cost, 4)                                               as avg_before,
       round(p.avg_cost, 4)                                               as avg_after,
       case when b.ledger_ct - p.ledger_ct = 2 * t.w and p.avg_cost = b.avg_cost
            then 'ok' else 'CHECK' end                                    as result
  from fix_before b
  join public.v_stock_position p using (grade_id, size_id)
  join (select grade_id, size_id, sum(weight_ct) w from public.stock_movement
         where movement_id in (select movement_id from fix_targets) group by 1, 2) t using (grade_id, size_id)
 order by b.grade_code, b.size_code;

commit;
