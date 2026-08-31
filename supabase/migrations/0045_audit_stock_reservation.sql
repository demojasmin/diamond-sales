-- ---------------------------------------------------------------------------
-- 0045  Reserving and releasing stock goes on the audit trail
--
-- WHY. 0043 gave a sales entry the power to take carats out of the available position the moment
-- a line is typed, and 0044 gave the office a way to see how much is spoken for. Neither left a
-- trace of WHO spoke for it or WHEN. Every other act that moves a figure on a screen -- an intake,
-- a sale, a receipt, a price change -- is on audit_log; a reservation was the one that was not,
-- and it is the one that can make a bucket read short with nothing on the ledger to explain it.
--
-- A DEDICATED TRIGGER, not the one the other tables share. That function predates these migrations
-- and its source is not in this repository, so attaching it to a new table would be guessing at a
-- contract nobody here can read. This writes the same five columns the shared one does, against a
-- table whose shape is known because 0043 created it.
--
-- record_id is the reservation's own key. It is short-lived by design -- a hold exists only until
-- the sale is confirmed or the carats go back -- so unlike an invoice the id will usually name a
-- row that is gone. That is the point: old_values on the DELETE says what was released, and the
-- INSERT before it says what was taken and by whom.
-- ---------------------------------------------------------------------------

create or replace function public.audit_stock_reservation()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    insert into public.audit_log (table_name, record_id, action, changed_by, old_values, new_values)
    values (
        'stock_reservation',
        coalesce(new.reservation_id, old.reservation_id),
        tg_op,
        -- auth.uid() is null when the write came from a SECURITY DEFINER path with nobody signed
        -- in -- release_on_post firing inside post_invoice, a migration, the SQL editor. The page
        -- already renders that as "System" rather than as an unknown person.
        auth.uid(),
        case when tg_op in ('UPDATE', 'DELETE') then to_jsonb(old) end,
        case when tg_op in ('INSERT', 'UPDATE') then to_jsonb(new) end
    );

    -- AFTER trigger: the return value is discarded, but returning null from a row-level trigger
    -- is a habit worth not forming here.
    return coalesce(new, old);
end;
$$;

comment on function public.audit_stock_reservation() is
    'Writes reserve, correct and release to audit_log (0045). Carats held by an unconfirmed sales entry are the one movement of a figure on screen that the trail did not carry.';

drop trigger if exists trg_audit_stock_reservation on public.stock_reservation;

create trigger trg_audit_stock_reservation
    after insert or update or delete on public.stock_reservation
    for each row execute function public.audit_stock_reservation();
