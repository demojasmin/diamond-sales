-- ---------------------------------------------------------------------------
-- Which of 0040 to 0045 is on THIS database?
--
-- READ ONLY. Selects from the catalogue and nothing else: no table is written, no function is
-- called, no row changes. Safe to run against the client's live database.
--
-- Run it on BOTH projects and compare the two result sets:
--   Demo   nzcvjaixgqoliyrotstz   (project "Priya gems")
--   Priya  obgclifsluxrgpluaqxs   (project "priyagems sales")
--
-- Each row is one migration. "yes" means the object that migration creates is present. Apply the
-- ones that say "no", IN NUMBER ORDER -- 0044 rewrites the view 0043 creates, and 0042 rewrites
-- the function 0040 creates, so out of order leaves the earlier definition standing.
-- ---------------------------------------------------------------------------
with checks as (
    select '0040  edit a posted invoice'  as migration, 1 as ord,
           to_regproc('public.edit_posted_invoice') is not null as present

    -- 0041 rewrites v_reconciliation rather than adding anything, so presence is not enough:
    -- the definition itself has to be read. It is the version that nets cancellations.
    union all
    select '0041  reconciliation nets cancellations', 2,
           coalesce(pg_get_viewdef('public.v_reconciliation'::regclass) ilike '%CANCELLED%', false)

    union all
    select '0042  edit an imported (MIG-) invoice', 3,
           to_regproc('public.imported_edit_delta') is not null

    union all
    select '0043  reserve stock as a line is typed', 4,
           to_regclass('public.stock_reservation') is not null
           and to_regproc('public.reserve_line') is not null

    -- 0044 adds two columns to the view 0043 created. The column is the whole migration.
    union all
    select '0044  ledger_ct on v_stock_position', 5,
           exists (select 1 from information_schema.columns
                    where table_schema = 'public' and table_name = 'v_stock_position'
                      and column_name = 'ledger_ct')

    union all
    select '0045  audit reserve and release', 6,
           exists (select 1 from pg_trigger
                    where tgname = 'trg_audit_stock_reservation' and not tgisinternal)
)
select migration,
       case when present then 'yes' else 'NO - apply it' end as applied
  from checks
 order by ord;
