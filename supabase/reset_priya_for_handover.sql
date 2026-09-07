-- ---------------------------------------------------------------------------
-- Empty the ledger for handover. Keep the setup.
--
-- GOES:  invoices, sales lines, receipts, stock movements, intake parcels,
--        rejection dispositions, stock reservations, the price list,
--        the audit trail, lockout counters -- AND every buyer and broker
--
-- STAYS: the grades, sizes and pairings, the currencies, the settings, and
--        both logins with their profiles
--
-- The client opens the app to an empty ledger with everything else working:
-- they can raise the first invoice, import their first stock sheet, and sign in
-- as themselves. The catalogue does not have to be set up again.
--
-- BUYERS AND BROKERS GO TOO, on the desk's instruction (Sept 2026). They used to
-- stay, and there was a case for it: they are the client's own trading partners,
-- imported from their own sheet, and keeping them meant an empty ledger against a
-- known customer list. The desk asked for a genuinely fresh start instead.
--
-- WHAT THAT COSTS, so it is not discovered later: the first sale to each customer
-- has to name them again. Nothing is lost that the client cannot retype or that a
-- sales import will not recreate -- the importer creates any buyer the sheet names
-- and the database does not have (docs/08 s2.4). Safe to delete only because the
-- invoices that pointed at them have already gone, a few lines above:
-- sales_invoice is the ONLY table with a foreign key to either.
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

-- ── the parties, once nothing points at them ───────────────────────────────
-- After the invoices, necessarily: sales_invoice.buyer_id and .broker_id are the
-- only foreign keys either table has, so these deletes are safe here and would
-- fail anywhere above.
delete from public.broker;
delete from public.buyer;

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
select setval(pg_get_serial_sequence('public.buyer',          'buyer_id'),    1, false);
select setval(pg_get_serial_sequence('public.broker',         'broker_id'),   1, false);

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
union all select 'lockout rows', count(*) from public.login_attempt
union all select 'buyers', count(*) from public.buyer
union all select 'brokers', count(*) from public.broker;

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
