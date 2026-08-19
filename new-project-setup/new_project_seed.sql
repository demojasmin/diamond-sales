-- ---------------------------------------------------------------------------
-- STEP 2 · The catalogue and settings, for a NEW, EMPTY project.
--
-- Run AFTER schema.sql. Contains NO customer data -- no buyers, no brokers,
-- no price list, no invoices, no stock. Those belong to whoever they belong to.
--
--   9 sieve sizes      27 grades      218 grade/size pairings
--   2 currencies       10 settings
--
-- The grades and their aliases ("NO-2", "1BB", ",-2 MB") are copied verbatim
-- from the working project, so the spellings the Excel and PDF importers
-- resolve through are the ones already proven against real sheets.
--
-- Safe to run twice: every insert ends ON CONFLICT DO NOTHING and the sequence
-- resets are idempotent.
-- ---------------------------------------------------------------------------

begin;

-- ═══════════════════════════════════════════════════════════════════════════
--  ►►►  THE ONE LINE TO EDIT  ◄◄◄
--
--  The client's company name, between the quotes. It appears on their
--  documents and in the app header. Nothing else in this file needs changing.
-- ═══════════════════════════════════════════════════════════════════════════

select set_config('app.client_name', 'REPLACE_WITH_CLIENT_NAME', true);


-- ── 1 · sieve sizes ────────────────────────────────────────────────────────
-- size_id 5 is absent on purpose: retired in the source project. The ids are
-- kept identical so nothing that references a size has to be rewritten.
-- 9 (+18) and 10 (+23) are NOT seeded: 0035 retired them, and they never carried a
-- movement, a parcel or a price in either project, so there is nothing to preserve.
-- 6 (14+) IS seeded, inactive. It held stock, so a project restored from this file
-- must still be able to read its own history; inactive keeps it out of every picker
-- and every import while leaving those rows resolvable.
insert into public.size_bucket (size_id, code, lower_mm, upper_mm, sort_order, active) overriding system value values
    (1,'-2',NULL,NULL,1,true),
    (2,'-6.5',NULL,NULL,2,true),
    (3,'+6.5',NULL,NULL,3,true),
    (4,'+11',NULL,NULL,4,true),
    (6,'14+',NULL,NULL,5,false),
    (7,'0.2',NULL,NULL,8,true),
    (8,'0.25',NULL,NULL,9,true)
on conflict do nothing;


-- ── 2 · grades ─────────────────────────────────────────────────────────────
-- Ids preserved. grade_id is what stock_movement, sales_line and price_list
-- point at, so letting these renumber would silently re-point holdings.
insert into public.grade (grade_id, code, display_name, aliases, sort_order, active) overriding system value values
    (1,'NO 1','No. 1 Clean','NO1;NO 1;1',1,true),
    (2,'NO 1 BB','No. 1 Bottom Black','1BB;1 BB;NO1BB;NO 1BB',2,true),
    (3,'NO 2','No. 2 Clean','NO2;2;NO-2',3,true),
    (4,'NO 2 BB','No. 2 Bottom Black','2BB;2 BB;NO2BB',4,true),
    (5,'NO II','No. II Spotted','II;NOII;NO2SPOT',5,true),
    (6,'NO DX','No. DX Deluxe','DX;NODX;DELUXE;NO-DX',6,true),
    (7,'EX 1','Extra No. 1','EX1;EXTRA 1;E1;Ex1',7,true),
    (8,'NO 3','No. 3','NO3;3;NO-3',8,true),
    (9,'NO 4','No. 4','NO4;4;NO-4',9,true),
    (10,'NO 5','No. 5','NO5;5;NO-5',10,true),
    (11,'NO 6','No. 6','NO6;6;NO-6',11,true),
    (12,'NO 7','No. 7','NO7;7;NO-7',12,true),
    (13,'TOP-COL','Top Colour','TOPCOL;TOP COL;TC;T COLOR',13,true),
    (14,'COL','Colour','COLOUR;COLOR;C',14,true),
    (15,'OW','Off White','OFFWHITE;OFF WHITE;O/W',15,true),
    (16,'LC 1','Light Colour 1','LC1;L C 1;LC-1',16,true),
    (17,'LC 2','Light Colour 2','LC2;L C 2;LC-2',17,true),
    (18,'LC 3','Light Colour 3','LC3;L C 3;LC-3',18,true),
    (19,'GH','Ghost','GHOST;GH-VVS;GHVVS',19,true),
    (20,'LB 1','Light Brown 1','LB1;L B 1;LB-1',20,true),
    (21,'LB 2','Light Brown 2','LB2;L B 2;LB-2',21,true),
    (22,'+14','Plus Fourteen','14;PLUS14;P14',22,true),
    (23,'EXTRA','Extra Assort','EXTRA ASSORT;XTRA;ASSORT',23,true),
    (24,'FL','FL','F L;FL1',24,true),
    (25,'1MB','1MB','1 MB;NO1MB;NO 1 MB',25,true),
    (26,'-2 MB','-2 MB',',-2 MB;-2MB;,-2MB',26,true),
    (27,'MIX','MIX',NULL,27,true)
on conflict do nothing;


-- ── 3 · which sizes each grade trades in ───────────────────────────────────
-- 0018's rule, stated once rather than as 218 rows: every grade takes every
-- size EXCEPT '-2', which is NO 1 and NO 1 BB only. Checked against the source
-- project -- it reproduces all 218 pairings exactly.
--
-- Without these a grade imports and can never be SOLD: 0018 guards sales_line
-- with a trigger against this table, and the failure reads as a bug rather
-- than as a missing catalogue row.
insert into public.grade_size (grade_id, size_id)
select g.grade_id, s.size_id
  from public.grade g
  cross join public.size_bucket s
 where s.code <> '-2'
    or g.code in ('NO 1', 'NO 1 BB')
on conflict do nothing;


-- ── 4 · currencies ─────────────────────────────────────────────────────────
-- USD carries a rate of 1.0 in the source project. If this client trades in
-- dollars, set a real rate before the first foreign-currency invoice.
insert into public.currency (currency_id, code, latest_rate_to_base) overriding system value values
    (1,'INR',1.000000),
    (2,'USD',1.000000)
on conflict do nothing;


-- ── 5 · settings ───────────────────────────────────────────────────────────
insert into public.app_config (key, value, description) values
    ('alert_low_stock_ct','40','CFG-004 carats below which a parcel is low'),
    ('alert_overdue_days','15','CFG-004 days past due before alerting'),
    ('base_currency','INR','CFG-001 base currency'),
    ('carat_precision','4','CFG-001 decimal places for carats'),
    ('company_name',current_setting('app.client_name'),'Shown on documents and the app header'),
    ('lockout_minutes','15',NULL),
    ('max_login_attempts','5','CFG-002 lockout threshold (AUTH-001 AC-4)'),
    ('money_precision','2','CFG-001 decimal places for money'),
    ('negative_stock','block','CFG-003: block | warn | allow'),
    ('session_timeout_min','60','CFG-002 session expiry')
on conflict do nothing;


-- ── 6 · the identity sequences ─────────────────────────────────────────────
-- The ids above went in with OVERRIDING SYSTEM VALUE, which does not advance
-- the sequence behind the column. Without these three lines the first grade
-- anyone adds through the app collides with grade_id 1.
select setval(pg_get_serial_sequence('public.size_bucket', 'size_id'),
              (select max(size_id)     from public.size_bucket));
select setval(pg_get_serial_sequence('public.grade',       'grade_id'),
              (select max(grade_id)    from public.grade));
select setval(pg_get_serial_sequence('public.currency',    'currency_id'),
              (select max(currency_id) from public.currency));


-- ── 7 · refuse to finish with the placeholder still in place ───────────────
do $$
declare v text;
begin
    select value into v from public.app_config where key = 'company_name';
    if v is null or v like '%REPLACE_WITH%' then
        raise exception
            'company_name is still the placeholder. Set it at the top of this file and run again.';
    end if;
end $$;

commit;


-- ---------------------------------------------------------------------------
-- Verification · expect 9 / 27 / 218 / 2 / 10, and the client's own name.
-- ---------------------------------------------------------------------------
select 'sizes' as thing, count(*)::text as value from public.size_bucket
union all select 'grades',   count(*)::text from public.grade
union all select 'pairings', count(*)::text from public.grade_size
union all select 'currency', count(*)::text from public.currency
union all select 'settings', count(*)::text from public.app_config
union all select 'company',  value          from public.app_config where key = 'company_name';
