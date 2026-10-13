-- ---------------------------------------------------------------------------
-- DEMO REHEARSAL of CORRECT_REPAIR_LOSS_1584_1587.sql. RUN ON DEMO ONLY.
--
-- Demo has no entries 1584-1587 (those numbers are Priya's), so this first recreates the same
-- mistake on Demo -- four +w manual adjustments with no rate, with the same weights and reasons,
-- in Demo's four largest buckets -- and then runs the Priya correction unchanged, except that its
-- targets are these four rehearsal rows.
--
-- The rehearsal rows go in directly, not through adjust_stock: since 0059 that function refuses
-- an increase with no rate, which is the bug being rehearsed. Refuses to run where entry 1584 is
-- one of the real ones (i.e. on Priya). ONE transaction: if the correction refuses, the
-- rehearsal rows go too and Demo is left exactly as it was.
-- ---------------------------------------------------------------------------
begin;

do $$
begin
    if exists (select 1 from public.stock_movement where movement_id = 1584 and reason = 'Repairing Loss') then
        raise exception 'This looks like Priya (entry 1584 is the real one). Run this on Demo only. Nothing was changed.';
    end if;
    if exists (select 1 from public.stock_movement where reason like 'Demo rehearsal of entry 158_:%') then
        raise exception 'The rehearsal has already been run on this database. Nothing was changed.';
    end if;
end;
$$;

-- Demo's four largest holdings with a cost stand in for Priya's four buckets (two of those are empty
-- on Demo, and a loss cannot come out of an empty bucket). Same weights, same reasons.
insert into public.stock_movement (movement_date, grade_id, size_id, movement_type, weight_ct, ref_type, reason)
select date '2026-10-12', b.grade_id, b.size_id, 'ADJUST', r.w, 'manual', 'Demo rehearsal of entry ' || r.id || ': ' || r.why
  from (values (1, 1584, 0.03, 'Repairing Loss'), (2, 1585, 0.07, 'Reparing Loss'),
               (3, 1586, 0.07, 'Repair Loss'),    (4, 1587, 0.02, 'Reparing Loss')) as r(n, id, w, why)
  join (select grade_id, size_id, row_number() over (order by balance_ct desc, grade_id, size_id) n
          from public.v_stock_position where balance_ct >= 1 and avg_cost > 0) b using (n);

-- ── the correction, as it will run on Priya (same transaction: a refusal undoes the rehearsal too) ──

-- THE TARGETS. The only line that differs between Priya and the Demo rehearsal.
create temporary table fix_targets on commit drop as
select movement_id from public.stock_movement where reason like 'Demo rehearsal of entry 158_:%';

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
