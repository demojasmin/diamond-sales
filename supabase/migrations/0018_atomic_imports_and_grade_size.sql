-- ---------------------------------------------------------------------------
-- 0018 · Make both imports atomic, and move two rules the client was holding
--        alone into the database.
--
-- WHY THIS EXISTS
--
-- Both imports are "replace, not merge". Both did it as a delete loop followed
-- by an insert loop, over separate HTTP calls, with no transaction around them:
--
--     DELETE chunk 1..n      <- old dataset gone
--     ...network fails...
--     INSERT chunk 1..n      <- never runs
--
-- That window is not theoretical. On 05 Aug 2026 it emptied the stock ledger:
-- 133 stock_movement rows deleted at 14:26:05, the replacement never landed,
-- and the position collapsed from 62 buckets / 2,344.34 ct to 5 buckets /
-- -220.85 ct with three negative balances. The SALE rows survived because the
-- ledger refuses DELETE to `authenticated`; only the intakes they were
-- subtracting from disappeared.
--
-- Both replacements now happen inside one function, so they are one
-- transaction. If anything raises -- a bad row, a lost connection, a payload
-- Postgres refuses -- the whole thing rolls back and the previous dataset is
-- still there. There is no longer any ordering in which data can be lost.
--
-- SECURITY DEFINER, for the same reason 0016 needed it: `authenticated` has no
-- DELETE on stock_movement and must never be granted it. Each function can
-- delete only rows it can prove are its own -- ref_type = 'stock_import' for
-- stock, invoice_no LIKE 'MIG-%' for sales. A hand-entered intake
-- ('rough_intake'), a posted sale ('sales_line') and a live invoice are all
-- unreachable from here. search_path is pinned so the definer rights cannot be
-- redirected at another schema's tables.
-- ---------------------------------------------------------------------------


-- ---------------------------------------------------------------------------
-- STOCK · replace the imported opening position, atomically.
--
-- p_rows is the workbook, already validated and resolved to ids by the client:
--   [{"grade_id":1,"size_id":2,"weight_ct":12.98,"price_per_ct":50740.00}, ...]
--
-- Supersedes delete_imported_stock() as the import path. That function is left
-- in place -- 0016 documents it, and it is still the right tool for "clear the
-- import and put nothing back".
-- ---------------------------------------------------------------------------
create or replace function public.replace_imported_stock(
    p_as_at date,
    p_rows  jsonb
)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_old_ids  bigint[];
    v_deleted  integer := 0;
    v_written  integer := 0;
begin
    if p_rows is null or jsonb_typeof(p_rows) <> 'array' then
        raise exception 'replace_imported_stock expects an array of rows';
    end if;

    -- Refuse to replace a real dataset with nothing. An empty array is what a
    -- parser returns when it silently failed to find the sheet, and "delete
    -- everything" is too destructive to be reachable by accident. Clearing the
    -- import on purpose is what delete_imported_stock() is for.
    if jsonb_array_length(p_rows) = 0 then
        raise exception 'replace_imported_stock was given no rows; use delete_imported_stock() to clear the import';
    end if;

    -- The parcels behind the movements, captured before the movements go.
    select coalesce(array_agg(distinct ref_id), '{}')
      into v_old_ids
      from public.stock_movement
     where ref_type = 'stock_import'
       and ref_id is not null;

    delete from public.stock_movement where ref_type = 'stock_import';
    get diagnostics v_deleted = row_count;

    if array_length(v_old_ids, 1) is not null then
        delete from public.rough_intake where intake_id = any(v_old_ids);
    end if;

    -- Parcels and their movements in one statement each, both reading the same
    -- CTE, so a movement without its intake is not expressible here.
    with parcel as (
        insert into public.rough_intake
            (intake_date, grade_id, size_id, weight_ct, price_per_ct, created_by)
        select p_as_at,
               (r->>'grade_id')::bigint,
               (r->>'size_id')::bigint,
               (r->>'weight_ct')::numeric,
               (r->>'price_per_ct')::numeric,
               auth.uid()
          from jsonb_array_elements(p_rows) as r
        returning intake_id, grade_id, size_id, weight_ct, price_per_ct
    )
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select p_as_at, p.grade_id, p.size_id, 'INTAKE', p.weight_ct,
           p.price_per_ct, 'stock_import', p.intake_id, auth.uid()
      from parcel p;

    get diagnostics v_written = row_count;

    return jsonb_build_object('ok', true, 'deleted', v_deleted, 'written', v_written);
end;
$$;


-- ---------------------------------------------------------------------------
-- SALES · replace the imported invoice history, atomically.
--
-- p_payload:
--   {"currency_id":1,
--    "invoices":[{"invoice_no":"MIG-1","invoice_date":"2024-08-14",
--                 "buyer_id":3,"broker_id":7,"broker_pct":1,"terms_days":30,
--                 "doc_type":"BILL","received":4180000.00,
--                 "lines":[{"grade_id":1,"size_id":2,"gross_weight_ct":232.86,
--                           "selection_ct":149.74,"price_per_ct":47251,
--                           "ex_rate":1,"less1_pct":1,"less2_pct":4,
--                           "remark":null}, ...]}, ...]}
--
-- Imported invoices are POSTED on arrival and deliberately write NO stock
-- movements: the imported opening balance is already net of these sales, so
-- deducting them again would double-count. That rule is unchanged -- it is why
-- this function never touches stock_movement at all.
--
-- ponytail: the whole file travels as one jsonb body (~1.5 MB for the 1,441
-- invoice workbook). That is the price of one transaction over one round trip.
-- If a bigger workbook ever exceeds the request limit the call is REFUSED
-- WHOLE, which is safe -- nothing is deleted, because the delete lives inside
-- this function. Stage-then-swap on a batch id if that day comes.
-- ---------------------------------------------------------------------------
create or replace function public.replace_imported_sales(p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_old_ids     bigint[];
    v_deleted     integer := 0;
    v_invoices    integer := 0;
    v_lines       integer := 0;
    v_receipts    integer := 0;
    v_currency    bigint;
begin
    if p_payload is null or jsonb_typeof(p_payload->'invoices') <> 'array' then
        raise exception 'replace_imported_sales expects {"invoices": [...]}';
    end if;

    if jsonb_array_length(p_payload->'invoices') = 0 then
        raise exception 'replace_imported_sales was given no invoices';
    end if;

    v_currency := (p_payload->>'currency_id')::bigint;
    if v_currency is null then
        raise exception 'replace_imported_sales needs a currency_id';
    end if;

    -- Only ever the previous import. A live invoice carries INV-yyyy-nnnnn and
    -- is not matched by this; 08 §4 is why migrated numbers are prefixed at all.
    select coalesce(array_agg(invoice_id), '{}')
      into v_old_ids
      from public.sales_invoice
     where invoice_no like 'MIG-%';

    if array_length(v_old_ids, 1) is not null then
        delete from public.receipt     where invoice_id = any(v_old_ids);
        delete from public.sales_line  where invoice_id = any(v_old_ids);
        delete from public.sales_invoice where invoice_id = any(v_old_ids);
        get diagnostics v_deleted = row_count;
    end if;

    -- Invoices first, keeping invoice_no as the handle to hang lines off: the
    -- ids are assigned by the sequence and the payload cannot know them.
    with incoming as (
        select inv from jsonb_array_elements(p_payload->'invoices') as inv
    ),
    written as (
        insert into public.sales_invoice
            (invoice_no, invoice_date, buyer_id, broker_id, broker_pct,
             terms_days, doc_type, currency_id, status,
             created_by, updated_by)
        select inv->>'invoice_no',
               (inv->>'invoice_date')::date,
               (inv->>'buyer_id')::bigint,
               nullif(inv->>'broker_id', '')::bigint,
               coalesce((inv->>'broker_pct')::numeric, 0),
               coalesce((inv->>'terms_days')::integer, 0),
               coalesce(inv->>'doc_type', 'BILL'),
               v_currency,
               'POSTED',
               auth.uid(),
               auth.uid()
          from incoming
        returning invoice_id, invoice_no
    )
    select count(*) into v_invoices from written;

    -- Lines, matched back by invoice_no.
    with incoming as (
        select inv->>'invoice_no' as no,
               jsonb_array_elements(coalesce(inv->'lines', '[]'::jsonb)) as ln
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct,
         price_per_ct, ex_rate, less1_pct, less2_pct, remark)
    select i.invoice_id,
           (c.ln->>'grade_id')::bigint,
           (c.ln->>'size_id')::bigint,
           (c.ln->>'gross_weight_ct')::numeric,
           (c.ln->>'selection_ct')::numeric,
           (c.ln->>'price_per_ct')::numeric,
           coalesce((c.ln->>'ex_rate')::numeric, 1),
           coalesce((c.ln->>'less1_pct')::numeric, 0),
           coalesce((c.ln->>'less2_pct')::numeric, 0),
           nullif(c.ln->>'remark', '')
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no;

    get diagnostics v_lines = row_count;

    -- One receipt per invoice that carried money, exactly as the workbook's
    -- single overwritten "Rec. Amt" cell states it (DQ-11: there is no payment
    -- history to migrate, only a running total). Dated the invoice date, since
    -- the sheet records no payment date -- docs/08 §5 says declare it, not bury
    -- it. Method 'IMPORTED', unchanged from the client-side importer.
    --
    -- `received` arrives ALREADY CAPPED at the invoice total. The cap stays on
    -- the client because it needs the line amounts CALC-1 produces, and those
    -- are the calculation engine's to compute, not this function's.
    with incoming as (
        select inv->>'invoice_no' as no,
               (inv->>'received')::numeric as received,
               (inv->>'invoice_date')::date as on_date
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.receipt (invoice_id, receipt_date, amount, method, created_by)
    select i.invoice_id, c.on_date, c.received, 'IMPORTED', auth.uid()
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no
     where c.received is not null and c.received > 0;

    get diagnostics v_receipts = row_count;

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'invoices', v_invoices, 'lines', v_lines,
                              'receipts', v_receipts);
end;
$$;


-- ---------------------------------------------------------------------------
-- MDM-004 · grade x size, in the database.
--
-- docs/04 §3.4: only NO 1 and NO 1 BB carry the -2 bucket; the other twenty
-- grades use three sizes. Until now the ONLY thing enforcing that was
-- Catalogue.SizesFor() in the WPF client -- its own comment says so. The API,
-- the Android app and psql could all write a combination the trade does not
-- have, and the desktop would then display a bucket nobody can sell from.
--
-- Seeded from what the grade sheets actually use, which is also what the stock
-- position already contains, so no existing row is invalidated by this.
-- ---------------------------------------------------------------------------
create table if not exists public.grade_size (
    grade_id bigint not null references public.grade(grade_id) on delete cascade,
    size_id  bigint not null references public.size_bucket(size_id) on delete cascade,
    primary key (grade_id, size_id)
);

alter table public.grade_size enable row level security;

-- Readable by anyone signed in; changed by nobody through the API. A new
-- grade/size pairing is a catalogue decision, not day-to-day data entry.
drop policy if exists grade_size_read on public.grade_size;
create policy grade_size_read on public.grade_size
    for select to authenticated using (true);

grant select on public.grade_size to authenticated;
revoke all on public.grade_size from anon;

-- Every grade takes every size EXCEPT '-2', which is NO 1 and NO 1 BB only.
insert into public.grade_size (grade_id, size_id)
select g.grade_id, s.size_id
  from public.grade g
  cross join public.size_bucket s
 where s.code <> '-2'
    or g.code in ('NO 1', 'NO 1 BB')
on conflict do nothing;

-- Any pairing already carrying stock or history is legitimate by definition,
-- whatever the rule above says. Adding them keeps this migration from turning
-- existing data into a constraint violation.
insert into public.grade_size (grade_id, size_id)
select distinct grade_id, size_id from public.stock_movement
on conflict do nothing;

insert into public.grade_size (grade_id, size_id)
select distinct grade_id, size_id from public.sales_line
on conflict do nothing;

-- Enforced on the way in, on the one table that matters. A trigger rather than
-- a foreign key: sales_line already keys grade_id and size_id separately, and
-- a composite FK would need both columns duplicated into a new unique index.
create or replace function public.sales_line_grade_size_guard()
returns trigger
language plpgsql
set search_path = public
as $$
begin
    if not exists (select 1 from public.grade_size
                    where grade_id = new.grade_id and size_id = new.size_id) then
        raise exception 'Grade % does not use size %',
            (select code from public.grade where grade_id = new.grade_id),
            (select code from public.size_bucket where size_id = new.size_id)
            using errcode = 'check_violation';
    end if;
    return new;
end;
$$;

drop trigger if exists sales_line_grade_size on public.sales_line;
create trigger sales_line_grade_size
    before insert or update of grade_id, size_id on public.sales_line
    for each row execute function public.sales_line_grade_size_guard();


-- ---------------------------------------------------------------------------
-- INV-005/006 · the rejection disposition table.
--
-- The Intake screen collects dispositions -- where each rejected parcel went --
-- and then discards them, because there was nowhere to put them. The app says
-- so honestly on every save, which is better than swallowing them, but it is
-- still a form that throws away what it collects.
--
-- Kept deliberately thin: a disposition is a rejected weight and the grade it
-- was reclassified into. It does not move stock -- CONVERT_IN / CONVERT_OUT
-- movements do that -- so this table records intent, not balance.
-- ---------------------------------------------------------------------------
create table if not exists public.rejection_disposition (
    disposition_id    bigserial primary key,
    movement_id       bigint not null references public.stock_movement(movement_id) on delete cascade,
    to_grade_id       bigint references public.grade(grade_id),
    weight_ct         numeric(12,4) not null check (weight_ct > 0),
    note              text,
    created_at        timestamptz not null default now(),
    created_by        uuid
);

create index if not exists rejection_disposition_movement
    on public.rejection_disposition (movement_id);

alter table public.rejection_disposition enable row level security;

drop policy if exists rejection_disposition_read on public.rejection_disposition;
create policy rejection_disposition_read on public.rejection_disposition
    for select to authenticated using (true);

-- Written by whoever may record the rejection it hangs off -- the same people
-- the UI already gates the stock-movement buttons on.
drop policy if exists rejection_disposition_write on public.rejection_disposition;
create policy rejection_disposition_write on public.rejection_disposition
    for insert to authenticated with check (true);

grant select, insert on public.rejection_disposition to authenticated;
grant usage, select on sequence public.rejection_disposition_disposition_id_seq to authenticated;
revoke all on public.rejection_disposition from anon;


-- ---------------------------------------------------------------------------
-- REJECT · same function, now able to keep the dispositions it is given.
--
-- The 6-argument version from 0014 is dropped rather than left beside this one:
-- Postgres would treat a 6-argument call as ambiguous between the old signature
-- and this one's default, and an ambiguous write path is worse than either.
--
-- Dispositions land in the SAME transaction as the movement they describe. A
-- disposition without its rejection is a row about nothing, and a rejection
-- whose dispositions were dropped is exactly the bug this closes.
-- ---------------------------------------------------------------------------
drop function if exists public.record_rejection(bigint, bigint, numeric, numeric, date, uuid);

create or replace function public.record_rejection(
    p_grade_id     bigint,
    p_size_id      bigint,
    p_weight_ct    numeric,
    p_price_per_ct numeric default null,
    p_date         date    default current_date,
    p_client_ref   uuid    default null,
    p_dispositions jsonb   default '[]'::jsonb
)
returns jsonb
language plpgsql
as $$
declare
    v_warning     text;
    v_movement_id bigint;
    v_kept        integer := 0;
begin
    if p_weight_ct is null or p_weight_ct <= 0 then
        raise exception 'Rejection weight must be greater than zero';
    end if;

    v_warning := public.assert_stock(p_grade_id, p_size_id, p_weight_ct);

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct,
         ref_type, created_by, client_ref)
    values
        (p_date, p_grade_id, p_size_id, 'REJECTION', p_weight_ct, p_price_per_ct,
         'manual', auth.uid(), p_client_ref)
    returning movement_id into v_movement_id;

    if jsonb_typeof(p_dispositions) = 'array' and jsonb_array_length(p_dispositions) > 0 then
        insert into public.rejection_disposition
            (movement_id, to_grade_id, weight_ct, note, created_by)
        select v_movement_id,
               nullif(d->>'to_grade_id', '')::bigint,
               (d->>'weight_ct')::numeric,
               nullif(d->>'note', ''),
               auth.uid()
          from jsonb_array_elements(p_dispositions) as d
         where (d->>'weight_ct')::numeric > 0;

        get diagnostics v_kept = row_count;
    end if;

    return jsonb_build_object('ok', true, 'warning', v_warning,
                              'movement_id', v_movement_id, 'dispositions', v_kept);
end;
$$;


-- ---------------------------------------------------------------------------
-- Two more workbook spellings, same shape as 0013.
--
-- The sale workbook grew rows written "Ex1" and "T COLOR" (Sale File Sample-1,
-- merged 04 Aug 2026). Neither resolves through the catalogue, so the importer
-- treated all three rows as unknown grades and SKIPPED them — quietly dropping
-- real sales out of the import. The regression suite catches it now; these
-- aliases are what make it pass.
--
-- Matching is case-insensitive (ExcelImport.AliasMap builds an
-- OrdinalIgnoreCase dictionary), so one spelling per variant is enough.
-- Additive and safe to re-run, exactly as 0013 is.
-- ---------------------------------------------------------------------------
update public.grade g
set    aliases = case
           when g.aliases is null or g.aliases = '' then t.alias
           else g.aliases || ';' || t.alias
       end
from  (values
          ('EX 1',    'Ex1'),
          ('TOP-COL', 'T COLOR')
      ) as t(code, alias)
where g.code = t.code
  and not (';' || coalesce(g.aliases, '') || ';') like ('%;' || t.alias || ';%');


grant execute on function public.replace_imported_stock(date, jsonb) to authenticated;
grant execute on function public.replace_imported_sales(jsonb)       to authenticated;
grant execute on function public.record_rejection(bigint, bigint, numeric, numeric,
                                                  date, uuid, jsonb) to authenticated;
