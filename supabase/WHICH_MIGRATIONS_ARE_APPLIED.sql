-- ---------------------------------------------------------------------------
-- Which of 0040 to 0052 is on THIS database?
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

    -- 0046 adds a column to a view that already existed, so the view proves nothing.
    union all
    select '0046  line_count on v_invoice', 7,
           exists (select 1 from information_schema.columns
                    where table_schema = 'public' and table_name = 'v_invoice'
                      and column_name = 'line_count')

    -- 0047 REMOVED 0038's negative-stock refusal from import_stock. It adds no object, so the only
    -- evidence is that the refusal is gone from the body -- present means 0038 is still standing.
    union all
    select '0047  stock import allows a negative position', 8,
           coalesce(pg_get_functiondef('public.import_stock'::regproc)
                    not ilike '%would be left below zero%', false)

    -- 0048, 0049, 0050 and 0051 each REWRITE replace_imported_sales, so the function existing
    -- proves nothing and they cannot be four rows: applying an earlier one over a later one undoes
    -- it. ONE row, for the newest, and the comment the function carries is the only evidence of
    -- which version is really installed -- they are written so the prefix is the number. What is
    -- actually there is printed underneath, so a "NO" says which version it has instead.
    union all
    select '0051  the import never drives stock negative', 9,
           coalesce(obj_description('public.replace_imported_sales'::regproc) like '0051.%', false)

    -- 0052 REMOVES 0026's MIG- exclusion from v_reconciliation, so this reads the opposite way to
    -- 0041's check above: the exclusion still being in the definition is the thing that means NO.
    union all
    select '0052  reconciliation counts imported sales', 10,
           coalesce(pg_get_viewdef('public.v_reconciliation'::regclass) not like '%MIG-%%', false)
)
select migration,
       case when present then 'yes' else 'NO - apply it' end as applied
  from checks
 order by ord;


-- ---------------------------------------------------------------------------
-- And which version of the sales importer is installed, in its own words.
--
-- 0048 to 0051 all leave a function of the same name behind, so this is the one
-- thing that separates them. The first line of the comment is the number.
--
--   0048  deducts only the sales dated after the last stock count
--   0049  deducts every imported line in full, position free to go negative
--   0050  the same, and reports the carat total to the import dialog
--   0051  caps each line at what its bucket holds, and reports what it could
--         not take as short_ct / short_buckets
-- ---------------------------------------------------------------------------
select coalesce(left(obj_description('public.replace_imported_sales'::regproc), 4),
                '(no comment - too old to say, or the function is missing)')
           as sales_importer_version,
       obj_description('public.replace_imported_sales'::regproc) as in_full;
