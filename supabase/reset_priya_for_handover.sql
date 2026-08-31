-- ---------------------------------------------------------------------------
-- Empty the ledger for handover. Keep the setup.
--
-- GOES:  invoices, sales lines, receipts, stock movements, intake parcels,
--        rejection dispositions, stock reservations, the price list,
--        the audit trail, lockout counters
--
-- STAYS: 27 grades, 9 sizes, 218 pairings, 2 currencies, 10 settings,
--        both logins and their profiles
--
-- The client opens the app to an empty ledger with everything else working:
-- they can raise the first invoice, import their first stock sheet, and sign in
-- as themselves. Nothing has to be set up again.
--
-- ONE TRANSACTION. It all happens or none of it does.
-- ---------------------------------------------------------------------------

begin;

-- ── the guard ──────────────────────────────────────────────────────────────
-- Both databases are called some form of "Priya" and both hold a company that
-- trades diamonds. The only thing that reliably tells them apart is this
-- setting, so the script refuses to run anywhere else rather than trusting
-- whoever pasted it to be in the right tab.
--
--     Priya (handover) : "Priya Sales"
--     Demo             : "Priya Gems"
do $$
declare v text;
begin
    select value into v from public.app_config where key = 'company_name';
    if v is distinct from 'Priya Sales' then
        raise exception
            'Refusing: this script empties the PRIYA handover database (company_name = "Priya Sales"). This one says "%".',
            coalesce(v, '(none)');
    end if;
end $$;


-- ── children first, so nothing is orphaned mid-way ─────────────────────────
delete from public.rejection_disposition;   -- points at stock_movement
delete from public.receipt;                 -- points at sales_invoice
delete from public.sales_line;              -- points at sales_invoice
delete from public.sales_invoice;
delete from public.stock_movement;
delete from public.rough_intake;
delete from public.price_list;
delete from public.login_attempt;

-- Carats a half-typed sales entry was holding (0043). Not a ledger row -- it never reached
-- stock_movement -- but it is netted out of v_stock_position, so a hold left behind would make
-- the handover database open reading short against an invoice that no longer exists and a screen
-- nobody can reach. The one row of state that survives the app being closed, so the one that a
-- reset has to be told about.
delete from public.stock_reservation;

-- ── the audit trail LAST, and this order is not arbitrary ──────────────────
-- Every delete above fires the audit trigger, which writes a DELETE row for
-- each one. Clearing this first would leave roughly four thousand fresh rows
-- recording the clearing itself -- a handover database whose only history is
-- the history being wiped.
delete from public.audit_log;


-- ── numbering starts again at 1 ────────────────────────────────────────────
-- Cosmetic, but the alternative is the client's first invoice arriving as
-- invoice_id 1439, which invites the question of what happened to the first
-- 1438 and has no good answer.
select setval(pg_get_serial_sequence('public.sales_invoice',  'invoice_id'),  1, false);
select setval(pg_get_serial_sequence('public.sales_line',     'line_id'),     1, false);
select setval(pg_get_serial_sequence('public.receipt',        'receipt_id'),  1, false);
select setval(pg_get_serial_sequence('public.stock_movement', 'movement_id'), 1, false);
select setval(pg_get_serial_sequence('public.rough_intake',   'intake_id'),   1, false);
select setval(pg_get_serial_sequence('public.audit_log',      'audit_id'),    1, false);
select setval(pg_get_serial_sequence('public.stock_reservation', 'reservation_id'), 1, false);

commit;


-- ---------------------------------------------------------------------------
-- Verification. The first block must all read 0; the second must not.
-- ---------------------------------------------------------------------------
select 'invoices' as emptied, count(*) from public.sales_invoice
union all select 'sales lines', count(*) from public.sales_line
union all select 'receipts', count(*) from public.receipt
union all select 'stock movements', count(*) from public.stock_movement
union all select 'intake parcels', count(*) from public.rough_intake
union all select 'rejection dispositions', count(*) from public.rejection_disposition
union all select 'price list', count(*) from public.price_list
union all select 'stock reservations', count(*) from public.stock_reservation
union all select 'audit rows', count(*) from public.audit_log
union all select 'lockout rows', count(*) from public.login_attempt;

select 'grades' as kept, count(*) from public.grade
union all select 'sizes', count(*) from public.size_bucket
union all select 'grade/size pairings', count(*) from public.grade_size
union all select 'currencies', count(*) from public.currency
union all select 'settings', count(*) from public.app_config
union all select 'logins', count(*) from public.profiles
union all select 'active owners', count(*) from public.profiles where role = 'owner' and active;

-- Both must be 0.0000. ledger_ct alongside balance_ct because they can only differ by a
-- reservation (0044), so reading them together proves the holds went as well as the movements.
select round(sum(balance_ct), 4) as available_now,
       round(sum(ledger_ct), 4)  as ledger_now,
       count(*)                  as buckets
  from public.v_stock_position;
