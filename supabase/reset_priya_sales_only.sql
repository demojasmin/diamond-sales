-- ---------------------------------------------------------------------------
-- Empty the SALES ledger. Leave the stock exactly where it is.
--
-- The narrower cousin of reset_priya_for_handover.sql, which empties everything.
-- This one is for a database whose stock is right and whose sales are not.
--
-- GOES:  sales invoices, their lines, their receipts, the stock movements those
--        sales wrote (SALE, REJECTION and the import_edit ADJUST corrections),
--        and any carats a half-typed sales entry is still holding.
--
-- STAYS: rough intake, hand-recorded rejections, conversions and adjustments,
--        the whole grade x size position they add up to, the price list, every
--        buyer and broker, the catalogue, the settings, the logins, the audit
--        trail.
--
--
-- THE ONE THING TO UNDERSTAND BEFORE RUNNING IT
--
-- THE CARATS COME BACK. A sale is not just a document -- it took weight out of a
-- bucket, as a SALE movement and, where part of the parcel was returned, a
-- REJECTION beside it. Deleting the invoice without deleting those movements
-- would leave the stock permanently short against a sale nobody can point at.
-- So they go together, and the position RISES by exactly what the deleted sales
-- had taken. That is the correct answer -- those sales no longer happened -- but
-- it is a change to the stock figure, and it is the reason this script reports
-- the position before and after rather than only counting rows.
--
-- WHICH MOVEMENTS ARE THE SALES' OWN. Two tags, and only these two:
--
--     ref_type = 'sales_line'   the SALE and REJECTION pair post_invoice writes
--                               for a line, and the same pair the sales importer
--                               writes for an imported one (0049-0051).
--     ref_type = 'import_edit'  the signed ADJUST that correcting a POSTED
--                               imported invoice leaves behind (0042). Its
--                               ref_id is the INVOICE, not a line, so it is not
--                               caught by the first tag and would otherwise be
--                               left holding a correction to an invoice that has
--                               gone.
--
-- Everything else in stock_movement -- INTAKE, CONVERT_IN, CONVERT_OUT, a
-- rejection or an adjustment recorded by hand on the Intake & movements page --
-- carries a different ref_type and is untouched. That is the whole of the line
-- between "sales data" and "stock data" here.
--
-- REJECTION DISPOSITIONS LOOK AFTER THEMSELVES. rejection_disposition.movement_id
-- is ON DELETE CASCADE (0018), so the dispositions belonging to a deleted
-- rejection go with it and must NOT be deleted separately -- doing so by hand
-- would also take the dispositions of hand-recorded rejections that are staying.
--
-- BUYERS AND BROKERS STAY. They are master data, not sales: the client's own
-- trading partners, and nothing in a cleared ledger requires them to go. The
-- handover script removes them because it is starting the database over; this
-- one is not. To remove them as well, add the two deletes named at the bottom.
--
-- THE AUDIT TRAIL STAYS, and will record these deletions. That is the point of
-- it: a ledger that was emptied should say so.
--
-- ONE TRANSACTION. It all happens or none of it does.
-- ---------------------------------------------------------------------------

begin;

-- ── the guard ──────────────────────────────────────────────────────────────
-- Both databases are called some form of "Priya" and both trade diamonds. This
-- setting is the only thing that reliably tells them apart, so the script
-- refuses to run anywhere else rather than trusting whoever pasted it to be in
-- the right tab.
--
--     Priya (handover) : "Priya Sales"     <- this script is set for this one
--     Demo             : "Priya Gems"
do $$
declare v text;
begin
    select value into v from public.app_config where key = 'company_name';
    if v is distinct from 'Priya Sales' then
        raise exception
            'Refusing: this script empties the SALES ledger of the Priya handover database (company_name = "Priya Sales"). This one says "%".',
            coalesce(v, '(none)');
    end if;
    raise notice 'Database confirmed: Priya Sales.';
end $$;


-- ── what is about to change, said before it changes ────────────────────────
do $$
declare
    v_inv   integer;
    v_lines integer;
    v_rec   integer;
    v_mov   integer;
    v_back  numeric;
    v_now   numeric;
begin
    select count(*) into v_inv   from public.sales_invoice;
    select count(*) into v_lines from public.sales_line;
    select count(*) into v_rec   from public.receipt;

    select count(*), coalesce(sum(weight_ct), 0) into v_mov, v_back
      from public.stock_movement
     where ref_type in ('sales_line', 'import_edit');

    select coalesce(round(sum(ledger_ct), 4), 0) into v_now from public.v_stock_position;

    raise notice 'Removing % invoice(s), % line(s), % receipt(s) and % sales movement(s).',
                 v_inv, v_lines, v_rec, v_mov;
    raise notice 'Stock now % ct; % ct comes back off those movements.', v_now, v_back;
end $$;


-- ── the sales movements FIRST ──────────────────────────────────────────────
-- Before the lines they are tagged to, so that if anything below fails the
-- transaction rolls back with the ledger and the documents still agreeing.
--
-- By ref_type alone, not by joining sales_line: a movement whose line has
-- already gone is exactly the orphan this is meant to clear, and a join would
-- step over it.
--
-- rejection_disposition rows cascade from here. Do not add a delete for them.
delete from public.stock_movement
 where ref_type in ('sales_line', 'import_edit');


-- ── carats a half-typed sales entry is holding (0043) ───────────────────────
-- Not a ledger row -- it never reached stock_movement -- but v_stock_position
-- nets it out of balance_ct, so a hold left behind would make the database read
-- short against an entry screen nobody can reach any more.
delete from public.stock_reservation;


-- ── the documents, children first ──────────────────────────────────────────
delete from public.receipt;                 -- points at sales_invoice
delete from public.sales_line;              -- points at sales_invoice
delete from public.sales_invoice;


-- ── numbering starts again at 1 ────────────────────────────────────────────
-- These three only. stock_movement and rough_intake keep their sequences: their
-- rows are still there, and resetting a sequence under live rows hands out ids
-- that already exist.
select setval(pg_get_serial_sequence('public.sales_invoice', 'invoice_id'), 1, false);
select setval(pg_get_serial_sequence('public.sales_line',    'line_id'),    1, false);
select setval(pg_get_serial_sequence('public.receipt',       'receipt_id'), 1, false);
select setval(pg_get_serial_sequence('public.stock_reservation', 'reservation_id'), 1, false);

commit;


-- ---------------------------------------------------------------------------
-- Verification. Every row of the first block must read 'ok'.
-- ---------------------------------------------------------------------------
select 'no invoices left' as check,
       case when (select count(*) from public.sales_invoice) = 0 then 'ok' else 'STILL THERE' end as status
union all
select 'no sales lines left',
       case when (select count(*) from public.sales_line) = 0 then 'ok' else 'STILL THERE' end
union all
select 'no receipts left',
       case when (select count(*) from public.receipt) = 0 then 'ok' else 'STILL THERE' end
union all
select 'no sales movements left',
       case when (select count(*) from public.stock_movement
                   where ref_type in ('sales_line', 'import_edit')) = 0
            then 'ok' else 'STILL THERE' end
union all
select 'no carats held by an entry',
       case when (select count(*) from public.stock_reservation) = 0 then 'ok' else 'STILL THERE' end
union all
-- The point of this script: the stock did NOT go with the sales.
select 'the stock is still here',
       case when (select count(*) from public.rough_intake) > 0
             and (select count(*) from public.stock_movement) > 0
            then 'ok' else 'STOCK WAS REMOVED TOO' end
union all
select 'and nothing is negative',
       case when (select count(*) from public.v_stock_position where ledger_ct < 0) = 0
            then 'ok' else 'A BUCKET IS BELOW ZERO' end
union all
select 'buyers and brokers kept',
       case when (select count(*) from public.buyer) > 0 then 'ok' else 'NONE LEFT' end
 order by 1;

-- What stock now reads. balance_ct and ledger_ct must be EQUAL: they differ only
-- by a reservation (0044), and every reservation has just gone.
select round(sum(ledger_ct), 4)  as ledger_now,
       round(sum(balance_ct), 4) as available_now,
       count(*) filter (where ledger_ct <> 0) as buckets_holding
  from public.v_stock_position;

-- The movements that remain, by type. No SALE and no REJECTION-from-a-sale
-- should appear; INTAKE and anything recorded by hand should.
select movement_type, ref_type, count(*) as rows, round(sum(weight_ct), 4) as carats
  from public.stock_movement
 group by movement_type, ref_type
 order by movement_type, ref_type;


-- ---------------------------------------------------------------------------
-- IF YOU ALSO WANT THE TRADING PARTNERS GONE, add these INSIDE the transaction
-- above, after the sales_invoice delete -- sales_invoice.buyer_id and .broker_id
-- are the only foreign keys either table has, so they are safe there and would
-- fail anywhere earlier:
--
--     delete from public.broker;
--     delete from public.buyer;
--     select setval(pg_get_serial_sequence('public.buyer',  'buyer_id'),  1, false);
--     select setval(pg_get_serial_sequence('public.broker', 'broker_id'), 1, false);
--
-- Nothing is lost that cannot be retyped: the sales importer creates any buyer
-- or broker a sheet names and the database does not have (docs/08 s2.4).
-- ---------------------------------------------------------------------------
