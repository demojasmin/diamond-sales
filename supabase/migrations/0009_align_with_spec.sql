-- ===========================================================================
-- Two places where 0001 departs from the agreed design. Both are cheap now and
-- expensive later: sales_invoice and stock_movement are still empty.
--
--
-- 1 · invoice_no must be NULLABLE
--
-- 0001 has `invoice_no varchar(20) not null unique`. docs/03-domain-model.md
-- §2.3 lists the opposite as a decision worth defending:
--
--     "invoice_no is assigned at post, not at create. Two offline clients
--      cannot both mint 'INV-0042'. The client owns invoice_id; the server
--      owns the human-readable number and assigns it in one transaction with
--      the post. Drafts have no number, which is honest — a draft is not yet
--      a document."
--
-- NOT NULL forces the opposite: every draft must mint a number up front. Two
-- desktops drafting offline then collide on the unique index, which is the
-- exact failure the design avoids (SYNC-001). It also burns a number on every
-- abandoned draft, leaving gaps in a document series that has to survive an
-- audit.
--
-- The constraint that actually expresses the rule is conditional: a POSTED
-- invoice must have a number, a DRAFT must not need one.
--
--
-- 2 · ADJUST needs a reason
--
-- docs/03 §2.4 carries `CHECK (movement_type <> 'ADJUST' OR reason IS NOT
-- NULL)` — an unexplained stock correction is indistinguishable from an error,
-- which is how the workbook's numbers drifted. 0001 has no `reason` column at
-- all. The desktop's adjustment screen already collects the text and has
-- nowhere to put it.
-- ===========================================================================

alter table public.sales_invoice alter column invoice_no drop not null;

alter table public.sales_invoice
    drop constraint if exists invoice_no_required_when_posted;
alter table public.sales_invoice
    add constraint invoice_no_required_when_posted
    check (status <> 'POSTED' or invoice_no is not null);

comment on column public.sales_invoice.invoice_no is
    'NULL until the invoice is posted. post_invoice() assigns it (docs/03 §2.3).';

alter table public.stock_movement add column if not exists reason varchar(500);

alter table public.stock_movement
    drop constraint if exists adjust_needs_a_reason;
alter table public.stock_movement
    add constraint adjust_needs_a_reason
    check (movement_type <> 'ADJUST' or reason is not null);

comment on column public.stock_movement.reason is
    'Mandatory on ADJUST. An unexplained correction is indistinguishable from '
    'an error (BR-INV-1).';
