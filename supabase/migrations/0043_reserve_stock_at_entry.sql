-- ---------------------------------------------------------------------------
-- 0043 · Stock falls as a line is entered, not when the sale is confirmed.
--
-- WHAT WAS ASKED FOR
--
--   Sales Entry   -> stock decreases, grade x size, as each line is completed
--   Confirm Sale  -> confirms the invoice, and must NOT deduct again
--   Move to Stock -> returns what that entry took, and never more
--   No double deduction, no duplicate movement
--
-- WHY THIS IS NOT A stock_movement
--
-- The obvious implementation is to write a SALE movement the moment a line has a grade, a size and
-- a weight. It cannot be, for three reasons, and none of them is a matter of taste:
--
--   v_reconciliation compares SALE movements against the lines of POSTED invoices. A SALE written
--   for a line nobody has confirmed puts its bucket out of balance the instant it is typed, and it
--   stays out until the sale is confirmed -- or for ever, if it never is.
--
--   A weight is TYPED, and typed things change. 10.00 corrected to 1.00 would leave two movements
--   and a third to net them, so the ledger fills with the history of somebody's keystrokes for a
--   parcel that may never be sold at all.
--
--   An entry abandoned -- the window closed, the app killed, the machine off -- would leave carats
--   deducted with no invoice anywhere to explain where they went.
--
-- A RESERVATION says the true thing instead: these carats are spoken for. It comes off the balance
-- every screen reads, so the stock page shows the reduced figure immediately, which is what was
-- asked for. It is not a movement, so the ledger and the reconciliation report are untouched. And
-- it can be changed, released or converted, because nothing has happened yet.
--
-- ONE ROW PER LINE, which is what makes duplication structural rather than something to remember:
-- unique (client_ref, line_key). Re-reserving the same line UPDATES its row. Type 10.00, correct it
-- to 1.00, correct it again -- one row throughout, holding whatever the line currently says.
--
-- AND THE HANDOVER TO post_invoice
--
-- Confirming converts: post_invoice writes the SALE and REJECTION movements as it always has, and
-- releases that entry's reservations in the SAME transaction. The carats are held by exactly one
-- mechanism at every instant -- reserved before, moved after, never both, never neither.
--
-- Move to Stock is release_entry: it deletes the rows that entry holds and the balance returns by
-- exactly what they held. It cannot return more than was taken, because there is nothing else it
-- can delete.
-- ---------------------------------------------------------------------------

begin;

-- ---------------------------------------------------------------------------
-- What is spoken for, and by which line of which entry.
--
-- client_ref is the entry on screen, which HAS one before it is saved (0007) -- so a line can be
-- reserved against an invoice that does not exist yet, which is the whole requirement.
--
-- line_key is the app's own handle for the row. It is text and not a line_id because sales_line
-- rows do not exist while somebody is typing.
-- ---------------------------------------------------------------------------
create table if not exists public.stock_reservation (
    reservation_id bigserial primary key,
    client_ref     uuid          not null,
    line_key       text          not null,
    grade_id       bigint        not null references public.grade(grade_id),
    size_id        bigint        not null references public.size_bucket(size_id),
    weight_ct      numeric(14,4) not null check (weight_ct >= 0),
    created_by     uuid,
    created_at     timestamptz   not null default now(),
    -- THE anti-duplication rule, in the schema rather than in anybody's memory.
    constraint stock_reservation_line_uq unique (client_ref, line_key)
);

create index if not exists stock_reservation_bucket_ix
    on public.stock_reservation (grade_id, size_id);

comment on table public.stock_reservation is
    '0043. Carats spoken for by a sales entry that has not been confirmed. Netted out of v_stock_position so every screen shows the reduced figure, but NOT a stock_movement: the ledger and v_reconciliation only ever see confirmed sales. One row per (client_ref, line_key), so editing a line updates its reservation instead of adding another.';

alter table public.stock_reservation enable row level security;

drop policy if exists stock_reservation_rw on public.stock_reservation;
create policy stock_reservation_rw on public.stock_reservation
    for all to authenticated using (true) with check (true);


-- ---------------------------------------------------------------------------
-- The position, less what is spoken for.
--
-- Identical to 0008 in every other respect: the movement arithmetic, the cost basis, the ageing.
-- Only balance_ct changes, and only by subtracting the reservations held against that bucket.
--
-- stock_value keeps using greatest(..., 0) on the RESERVED balance too, so a bucket reserved down
-- to nothing is worth nothing rather than a negative figure.
-- ---------------------------------------------------------------------------
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
    current_date - min(mv.inward_date)        as age_days
from public.grade g
cross join public.size_bucket s
left join movement mv on mv.grade_id = g.grade_id and mv.size_id = s.size_id
left join held h      on h.grade_id  = g.grade_id and h.size_id  = s.size_id
group by g.grade_id, g.code, g.display_name, s.size_id, s.code;

comment on view public.v_stock_position is
    'CALC-6/7. Balance, average cost and value per grade x size. Since 0043 the balance is net of stock_reservation: carats a sales entry has spoken for are already out of it, so the figure on screen is what is actually available to sell.';


-- ---------------------------------------------------------------------------
-- Hold what a line says. Called again whenever that line changes.
--
-- UPSERT on (client_ref, line_key), so a weight corrected from 10 to 1 leaves one reservation
-- holding 1 -- not two holding 11. That is the no-duplicate rule, and it is the schema's to keep.
--
-- Refuses to take a bucket negative, against the same policy posting uses. The check is made
-- against the balance WITH this line's existing reservation excluded, or correcting 10.00 down to
-- 9.00 would be measured against a balance the old 10.00 was still being subtracted from.
-- ---------------------------------------------------------------------------
create or replace function public.reserve_line(
    p_client_ref uuid,
    p_line_key   text,
    p_grade_id   bigint,
    p_size_id    bigint,
    p_weight_ct  numeric
)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_available numeric;
    v_policy    text;
begin
    if p_client_ref is null or coalesce(btrim(p_line_key), '') = '' then
        raise exception 'reserve_line needs an entry and a line';
    end if;

    if p_weight_ct is null or p_weight_ct <= 0 then
        -- Nothing to hold. A line emptied back out releases instead of reserving zero, so no row
        -- is left behind claiming nothing.
        delete from public.stock_reservation
         where client_ref = p_client_ref and line_key = p_line_key;
        return jsonb_build_object('ok', true, 'reserved', 0);
    end if;

    select coalesce(sp.balance_ct, 0)
           + coalesce((select r.weight_ct from public.stock_reservation r
                        where r.client_ref = p_client_ref and r.line_key = p_line_key), 0)
      into v_available
      from public.v_stock_position sp
     where sp.grade_id = p_grade_id and sp.size_id = p_size_id;

    v_policy := public.negative_stock_policy();

    if v_policy = 'block' and coalesce(v_available, 0) < p_weight_ct then
        raise exception 'Only % ct available in that bucket', coalesce(v_available, 0)
            using errcode = 'check_violation';
    end if;

    insert into public.stock_reservation
        (client_ref, line_key, grade_id, size_id, weight_ct, created_by)
    values (p_client_ref, p_line_key, p_grade_id, p_size_id, p_weight_ct, auth.uid())
    on conflict (client_ref, line_key) do update
        set grade_id  = excluded.grade_id,
            size_id   = excluded.size_id,
            weight_ct = excluded.weight_ct;

    return jsonb_build_object('ok', true, 'reserved', p_weight_ct);
end;
$$;


-- ---------------------------------------------------------------------------
-- Give one line's carats back. Removing a line, or emptying it.
-- ---------------------------------------------------------------------------
create or replace function public.release_line(p_client_ref uuid, p_line_key text)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare v_ct numeric := 0;
begin
    delete from public.stock_reservation
     where client_ref = p_client_ref and line_key = p_line_key
    returning weight_ct into v_ct;

    return jsonb_build_object('ok', true, 'returned', coalesce(v_ct, 0));
end;
$$;


-- ---------------------------------------------------------------------------
-- MOVE TO STOCK. Gives back everything this entry is holding, and nothing else.
--
-- It cannot return more than was taken: the only rows it can delete are the ones this entry holds,
-- and each holds exactly what its line last said. There is no figure passed in to get wrong.
--
-- Idempotent by the same fact. Pressing it twice returns the carats once and then reports zero,
-- because the second call finds nothing left to delete.
-- ---------------------------------------------------------------------------
create or replace function public.release_entry(p_client_ref uuid)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare v_ct numeric; v_n integer;
begin
    with gone as (
        delete from public.stock_reservation
         where client_ref = p_client_ref
        returning weight_ct
    )
    select coalesce(sum(weight_ct), 0), count(*) into v_ct, v_n from gone;

    return jsonb_build_object('ok', true, 'returned', v_ct, 'lines', v_n);
end;
$$;


-- ---------------------------------------------------------------------------
-- Confirming converts a hold into a movement. It must not do both.
--
-- post_invoice already writes the SALE and REJECTION movements; this releases the same entry's
-- reservations in that transaction, so at no instant are the carats subtracted twice. Either the
-- reservation holds them, or the movements do.
--
-- A trigger, not an edit to post_invoice: that function is 0019's and is rewritten by later
-- migrations, and a hook that survives being rewritten is worth more than one that has to be
-- re-applied every time somebody touches it.
-- ---------------------------------------------------------------------------
create or replace function public.release_on_post() returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    if new.status = 'POSTED' and coalesce(old.status, '') <> 'POSTED'
       and new.client_ref is not null then
        delete from public.stock_reservation where client_ref = new.client_ref;
    end if;
    return new;
end;
$$;

drop trigger if exists sales_invoice_release_reservations on public.sales_invoice;
create trigger sales_invoice_release_reservations
    after update on public.sales_invoice
    for each row execute function public.release_on_post();

comment on function public.release_on_post is
    '0043. When an invoice becomes POSTED its entry stops holding reservations: the SALE and REJECTION movements post_invoice writes are now what subtract those carats. Without this the same parcel would be out of stock twice.';


revoke all on function public.reserve_line(uuid, text, bigint, bigint, numeric) from public, anon;
revoke all on function public.release_line(uuid, text) from public, anon;
revoke all on function public.release_entry(uuid) from public, anon;
grant execute on function public.reserve_line(uuid, text, bigint, bigint, numeric) to authenticated;
grant execute on function public.release_line(uuid, text) to authenticated;
grant execute on function public.release_entry(uuid) to authenticated;
grant select, insert, update, delete on public.stock_reservation to authenticated;
grant usage, select on sequence public.stock_reservation_reservation_id_seq to authenticated;

commit;
