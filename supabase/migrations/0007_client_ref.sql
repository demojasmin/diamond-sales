-- ===========================================================================
-- SYNC-001 · idempotency keys
--
-- The offline outbox replays queued writes on reconnect. The failure mode is a
-- retry after a LOST RESPONSE — the insert succeeded, the acknowledgement did
-- not, so the client sends it again. `invoice_no` is unique so invoices are
-- already safe; receipts and stock movements are not, and a duplicated SALE
-- movement silently corrupts the balance with nothing to point at afterwards.
--
-- A unique uuid per queued operation makes the replay a no-op: the second
-- insert violates the constraint and is discarded.
--
-- Postgres treats NULLs as distinct in a unique index, so existing rows and any
-- write that did not come from the outbox are unaffected.
-- ===========================================================================

alter table public.sales_invoice  add column if not exists client_ref uuid;
alter table public.receipt        add column if not exists client_ref uuid;
alter table public.stock_movement add column if not exists client_ref uuid;

create unique index if not exists sales_invoice_client_ref_uq
    on public.sales_invoice (client_ref) where client_ref is not null;
create unique index if not exists receipt_client_ref_uq
    on public.receipt (client_ref) where client_ref is not null;
create unique index if not exists stock_movement_client_ref_uq
    on public.stock_movement (client_ref) where client_ref is not null;

comment on column public.sales_invoice.client_ref is
    'Client-generated GUID. Makes an offline replay idempotent (SYNC-001).';
