-- ---------------------------------------------------------------------------
-- Empty the DEMO ledger so an import can be tested against a clean slate.
--
-- GOES:  invoices, sales lines, receipts, stock movements, intake parcels,
--        rejection dispositions, the audit trail, lockout counters
--
-- STAYS: grades, sizes, grade/size pairings, currencies, settings, buyers,
--        brokers, every login and profile
--
-- WHY THIS EXISTS
--
-- Demo has a day of testing in it: rejections and sales recorded against stock
-- imports that were then replaced by smaller sheets. That is what makes a fresh
-- import refuse -- 114.18 ct has gone out of a bucket a new sheet fills with
-- 95.88 ct, so the replacement would leave it at -18.30 ct.
--
-- The refusal is correct and must stay. What is wrong is the data underneath it,
-- and the honest fix is to clear the test movements rather than to weaken the
-- rule that noticed them.
--
-- THE GUARD
--
-- Both databases hold a company trading diamonds and both are called some form
-- of "Priya". The only thing that reliably tells them apart is this setting, so
-- the script refuses to run anywhere else rather than trusting whoever pasted
-- it to be in the right browser tab.
--
--     Demo             : "Priya Gems"   <- this script
--     Priya (handover) : "Priya Sales"  <- refuses
--
-- ONE TRANSACTION. It all happens or none of it does.
-- ---------------------------------------------------------------------------

begin;

do $$
declare v text;
begin
    select value into v from public.app_config where key = 'company_name';
    if v is distinct from 'Priya Gems' then
        raise exception
            'Refusing: this script empties the DEMO database (company_name = "Priya Gems"). This one says "%". If you meant the handover database, reset_priya_for_handover.sql is the one that names it.',
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

-- ── the audit trail LAST, and the order is not arbitrary ───────────────────
-- Every delete above fires the audit trigger, which writes a DELETE row for each
-- one. Clearing this first would leave thousands of fresh rows recording the
-- clearing itself.
delete from public.audit_log;


-- ── numbering starts again at 1 ────────────────────────────────────────────
select setval(pg_get_serial_sequence('public.sales_invoice',  'invoice_id'),  1, false);
select setval(pg_get_serial_sequence('public.sales_line',     'line_id'),     1, false);
select setval(pg_get_serial_sequence('public.receipt',        'receipt_id'),  1, false);
select setval(pg_get_serial_sequence('public.stock_movement', 'movement_id'), 1, false);
select setval(pg_get_serial_sequence('public.rough_intake',   'intake_id'),   1, false);
select setval(pg_get_serial_sequence('public.audit_log',      'audit_id'),    1, false);

commit;


-- ---------------------------------------------------------------------------
-- Verification. Everything above the line must read 0; everything below it
-- must not.
-- ---------------------------------------------------------------------------
select 'invoices' as item, count(*)::text as n, 'must be 0' as expect from public.sales_invoice
union all select 'sales lines',    count(*)::text, 'must be 0' from public.sales_line
union all select 'receipts',       count(*)::text, 'must be 0' from public.receipt
union all select 'stock movements',count(*)::text, 'must be 0' from public.stock_movement
union all select 'intake parcels', count(*)::text, 'must be 0' from public.rough_intake
union all select 'rejections',     count(*)::text, 'must be 0' from public.rejection_disposition
union all select 'price list',     count(*)::text, 'must be 0' from public.price_list
union all select 'audit rows',     count(*)::text, 'must be 0' from public.audit_log
union all select 'negative buckets',
       (select count(*)::text from public.v_stock_position where balance_ct < 0), 'must be 0'
union all select '- - -', '-', '- - -'
union all select 'grades',    count(*)::text, 'kept' from public.grade
union all select 'sizes',     count(*)::text, 'kept' from public.size_bucket
union all select 'pairings',  count(*)::text, 'kept' from public.grade_size
union all select 'currencies',count(*)::text, 'kept' from public.currency
union all select 'settings',  count(*)::text, 'kept' from public.app_config
union all select 'buyers',    count(*)::text, 'kept' from public.buyer
union all select 'brokers',   count(*)::text, 'kept' from public.broker
union all select 'logins',    count(*)::text, 'kept' from public.profiles;
