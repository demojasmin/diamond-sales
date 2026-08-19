


SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;


COMMENT ON SCHEMA "public" IS 'standard public schema';



CREATE EXTENSION IF NOT EXISTS "pg_stat_statements" WITH SCHEMA "extensions";






CREATE EXTENSION IF NOT EXISTS "pgcrypto" WITH SCHEMA "extensions";






CREATE EXTENSION IF NOT EXISTS "supabase_vault" WITH SCHEMA "vault";






CREATE EXTENSION IF NOT EXISTS "uuid-ossp" WITH SCHEMA "extensions";






CREATE OR REPLACE FUNCTION "public"."add_grade"("p_code" "text", "p_display_name" "text" DEFAULT NULL::"text") RETURNS "jsonb"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
declare
    v_code text := btrim(coalesce(p_code, ''));
    v_id   bigint;
    v_new  boolean := false;
begin
    if v_code = '' then
        raise exception 'add_grade needs a grade code';
    end if;

    -- A guard, not a validation rule. Grade codes are trade shorthand -- "GH",
    -- "TOP-COL", "-2 MB" -- so almost anything short is legitimate. What this
    -- catches is a whole table row arriving as a name, which is exactly what a
    -- misread PDF produces ("1BB 6.01 11.80 3.05 1 BB").
    if length(v_code) > 20 then
        raise exception 'add_grade: "%" is too long to be a grade code', v_code;
    end if;

    -- Existing code OR existing alias. Without the alias arm, adding "DX" would
    -- create a second grade beside NO DX, and the stock would split across two
    -- rows that are the same goods. The ';' sentinels are 0013's idiom: bare
    -- '%DX%' would also match a hypothetical "DX1".
    select grade_id into v_id
      from public.grade
     where code = v_code
        or (';' || coalesce(aliases, '') || ';') like ('%;' || v_code || ';%')
     limit 1;

    if v_id is null then
        insert into public.grade (code, display_name, sort_order, active)
        values (v_code,
                coalesce(nullif(btrim(p_display_name), ''), v_code),
                (select coalesce(max(sort_order), 0) + 1 from public.grade),
                true)
        returning grade_id into v_id;
        v_new := true;
    end if;

    -- The size pairings, or the grade imports and can never be SOLD. 0018 puts a
    -- trigger on sales_line against grade_size; a grade added without pairings
    -- passes the stock import -- that trigger is on sales_line, not
    -- stock_movement -- and then fails at the first invoice with "Grade MIX does
    -- not use size 14+", which reads as a bug rather than as a missing row.
    --
    -- Same rule as 0018 and 0029: every size except '-2', which is NO 1 and
    -- NO 1 BB only.
    insert into public.grade_size (grade_id, size_id)
    select v_id, s.size_id
      from public.size_bucket s
     where s.code <> '-2'
    on conflict do nothing;

    return jsonb_build_object('ok', true, 'grade_id', v_id,
                              'created', v_new, 'code', v_code);
end;
$$;


ALTER FUNCTION "public"."add_grade"("p_code" "text", "p_display_name" "text") OWNER TO "postgres";


COMMENT ON FUNCTION "public"."add_grade"("p_code" "text", "p_display_name" "text") IS 'Adds a grade named on a stock sheet that the catalogue does not have, with the size pairings 0018''s rule gives it. Idempotent, and returns the existing grade when the code is already a code or an alias.';



CREATE OR REPLACE FUNCTION "public"."adjust_stock"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_reason" "text", "p_date" "date" DEFAULT CURRENT_DATE, "p_client_ref" "uuid" DEFAULT NULL::"uuid") RETURNS "jsonb"
    LANGUAGE "plpgsql"
    AS $$ declare v_warning text; begin if p_weight_ct is null or p_weight_ct = 0 then raise exception 'An adjustment must move a non-zero weight'; end if; if p_reason is null or btrim(p_reason) = '' then raise exception 'An adjustment reason is required'; end if; if p_weight_ct < 0 then v_warning := public.assert_stock(p_grade_id, p_size_id, -p_weight_ct); end if; insert into public.stock_movement (movement_date, grade_id, size_id, movement_type, weight_ct, ref_type, reason, created_by, client_ref) values (p_date, p_grade_id, p_size_id, 'ADJUST', p_weight_ct, 'manual', btrim(p_reason), auth.uid(), p_client_ref); return jsonb_build_object('ok', true, 'warning', v_warning); end; $$;


ALTER FUNCTION "public"."adjust_stock"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_reason" "text", "p_date" "date", "p_client_ref" "uuid") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."adjust_stock_at_cost"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric, "p_reason" "text", "p_date" "date" DEFAULT CURRENT_DATE, "p_client_ref" "uuid" DEFAULT NULL::"uuid") RETURNS "jsonb"
    LANGUAGE "plpgsql"
    AS $$
declare
    v_warning text;
begin
    if p_weight_ct is null or p_weight_ct = 0 then
        raise exception 'An adjustment must move a non-zero weight';
    end if;

    if p_reason is null or btrim(p_reason) = '' then
        raise exception 'An adjustment reason is required';
    end if;

    if p_price_per_ct is not null and p_price_per_ct < 0 then
        raise exception 'A rate cannot be negative';
    end if;

    -- Adding carats at no stated rate is what diluted this ledger's cost in
    -- the first place. Refused rather than accepted quietly: the caller knows
    -- the rate, or the correction is not ready to be made.
    if p_weight_ct > 0 and coalesce(p_price_per_ct, 0) = 0 then
        raise exception 'Adding % ct needs the rate those carats are worth, or the average cost of this bucket is destroyed', p_weight_ct;
    end if;

    if p_weight_ct < 0 then
        v_warning := public.assert_stock(p_grade_id, p_size_id, -p_weight_ct);
    end if;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, reason, created_by, client_ref)
    values
        (p_date, p_grade_id, p_size_id, 'ADJUST', p_weight_ct,
         case when p_weight_ct > 0 then p_price_per_ct end,
         'manual', btrim(p_reason), auth.uid(), p_client_ref);

    return jsonb_build_object('ok', true, 'warning', v_warning);
end;
$$;


ALTER FUNCTION "public"."adjust_stock_at_cost"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric, "p_reason" "text", "p_date" "date", "p_client_ref" "uuid") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."assert_stock"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric) RETURNS "text"
    LANGUAGE "plpgsql"
    AS $$ declare v_balance numeric; v_grade text; v_size text; v_policy text; begin if p_weight_ct is null or p_weight_ct <= 0 then return null; end if; v_policy := public.negative_stock_policy(); if v_policy not in ('block', 'warn') then return null; end if; select balance_ct, grade_code, size_code into v_balance, v_grade, v_size from public.v_stock_position where grade_id = p_grade_id and size_id = p_size_id; if coalesce(v_balance, 0) >= p_weight_ct then return null; end if; if v_policy = 'block' then raise exception 'Only % ct on hand for % x %, cannot take out % ct', coalesce(v_balance, 0), coalesce(v_grade, '?'), coalesce(v_size, '?'), p_weight_ct; end if; return format('This takes %s x %s below zero - %s ct on hand, %s ct going out.', coalesce(v_grade, '?'), coalesce(v_size, '?'), coalesce(v_balance, 0), p_weight_ct); end; $$;


ALTER FUNCTION "public"."assert_stock"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric) OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."audit_change"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public', 'pg_temp'
    AS $$
declare
    v_old    jsonb;
    v_new    jsonb;
    v_id     bigint;
    v_key    text := tg_argv[0];
    v_diff   jsonb := '{}'::jsonb;
    v_before jsonb := '{}'::jsonb;
    k        text;
begin
    if tg_op = 'DELETE' then
        v_old := to_jsonb(old);
        v_id  := (v_old ->> v_key)::bigint;
        insert into public.audit_log (table_name, record_id, action, changed_by, old_values)
        values (tg_table_name, v_id, 'DELETE', auth.uid(), v_old);
        return old;
    end if;

    v_new := to_jsonb(new);
    v_id  := (v_new ->> v_key)::bigint;

    if tg_op = 'INSERT' then
        insert into public.audit_log (table_name, record_id, action, changed_by, new_values)
        values (tg_table_name, v_id, 'INSERT', auth.uid(), v_new);
        return new;
    end if;

    -- UPDATE: keep only genuinely changed fields.
    v_old := to_jsonb(old);
    for k in select jsonb_object_keys(v_new) loop
        if v_new -> k is distinct from v_old -> k and k <> 'updated_at' then
            v_diff   := v_diff   || jsonb_build_object(k, v_new -> k);
            v_before := v_before || jsonb_build_object(k, v_old -> k);
        end if;
    end loop;

    -- A no-op save is not history worth keeping.
    if v_diff = '{}'::jsonb then
        return new;
    end if;

    insert into public.audit_log (table_name, record_id, action, changed_by, old_values, new_values)
    values (tg_table_name, v_id, 'UPDATE', auth.uid(), v_before, v_diff);
    return new;
end;
$$;


ALTER FUNCTION "public"."audit_change"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."cancel_invoice"("p_invoice_id" bigint, "p_reason" "text" DEFAULT NULL::"text") RETURNS "jsonb"
    LANGUAGE "plpgsql"
    AS $$
declare
    v_status text;
begin
    select status into v_status
      from public.sales_invoice where invoice_id = p_invoice_id;

    if not found then
        raise exception 'Invoice % not found', p_invoice_id using errcode = 'no_data_found';
    end if;

    if v_status = 'CANCELLED' then
        return jsonb_build_object('ok', true, 'already_cancelled', true);
    end if;

    -- FR-SALES-12: the reason is mandatory, and 0009 enforces it on the ADJUST rows.
    if p_reason is null or btrim(p_reason) = '' then
        raise exception 'Cancelling an invoice requires a reason';
    end if;

    -- Only a POSTED invoice moved stock, so only that one needs reversing.
    if v_status = 'POSTED' then
        insert into public.stock_movement
            (movement_date, grade_id, size_id, movement_type, weight_ct,
             price_per_ct, ref_type, ref_id, created_by, reason)
        select current_date, m.grade_id, m.size_id, 'ADJUST', m.weight_ct,
               m.price_per_ct, 'cancel', p_invoice_id, auth.uid(),
               'Reversal of invoice ' || p_invoice_id || ': ' || p_reason
          from public.stock_movement m
          join public.sales_line l on l.line_id = m.ref_id
         where m.ref_type = 'sales_line'
           and l.invoice_id = p_invoice_id
           and m.movement_type in ('SALE', 'REJECTION');
    end if;

    update public.sales_invoice
       set status = 'CANCELLED', updated_by = auth.uid()
     where invoice_id = p_invoice_id;

    return jsonb_build_object('ok', true, 'reason', p_reason);
end;
$$;


ALTER FUNCTION "public"."cancel_invoice"("p_invoice_id" bigint, "p_reason" "text") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."clear_login_failures"("p_email" "text") RETURNS "void"
    LANGUAGE "sql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$ delete from public.login_attempt where email = lower(trim(p_email)); $$;


ALTER FUNCTION "public"."clear_login_failures"("p_email" "text") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."convert_stock"("p_from_grade_id" bigint, "p_from_size_id" bigint, "p_to_grade_id" bigint, "p_to_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric DEFAULT NULL::numeric, "p_date" "date" DEFAULT CURRENT_DATE, "p_client_ref" "uuid" DEFAULT NULL::"uuid") RETURNS "jsonb"
    LANGUAGE "plpgsql"
    AS $$ declare v_warning text; v_price numeric; v_from text; begin if p_weight_ct is null or p_weight_ct <= 0 then raise exception 'Conversion weight must be greater than zero'; end if; if p_from_grade_id = p_to_grade_id and p_from_size_id = p_to_size_id then raise exception 'Cannot convert a parcel into itself'; end if; if p_price_per_ct is not null and p_price_per_ct <= 0 then raise exception 'A conversion price of % is not valid. Leave it blank to carry the source bucket''s cost, or enter what the carats are worth.', p_price_per_ct; end if; v_warning := public.assert_stock(p_from_grade_id, p_from_size_id, p_weight_ct); if p_price_per_ct is null then select nullif(avg_cost, 0), grade_code || ' x ' || size_code into v_price, v_from from public.v_stock_position where grade_id = p_from_grade_id and size_id = p_from_size_id; if v_price is null then raise exception 'No cost is recorded for %, so there is nothing for this conversion to carry. Enter a price per carat.', coalesce(v_from, 'that bucket'); end if; else v_price := p_price_per_ct; end if; insert into public.stock_movement (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct, ref_type, counterparty_grade_id, created_by, client_ref) values (p_date, p_from_grade_id, p_from_size_id, 'CONVERT_OUT', p_weight_ct, v_price, 'conversion', p_to_grade_id, auth.uid(), p_client_ref), (p_date, p_to_grade_id, p_to_size_id, 'CONVERT_IN', p_weight_ct, v_price, 'conversion', p_from_grade_id, auth.uid(), null); return jsonb_build_object('ok', true, 'warning', v_warning, 'price_per_ct', v_price); end; $$;


ALTER FUNCTION "public"."convert_stock"("p_from_grade_id" bigint, "p_from_size_id" bigint, "p_to_grade_id" bigint, "p_to_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric, "p_date" "date", "p_client_ref" "uuid") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."current_user_role"() RETURNS "text"
    LANGUAGE "sql" STABLE SECURITY DEFINER
    SET "search_path" TO 'public', 'pg_temp'
    AS $$
    select role from public.profiles where id = auth.uid() and active
$$;


ALTER FUNCTION "public"."current_user_role"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."dashboard_summary"("p_from" "date", "p_to" "date") RETURNS TABLE("sales_amount" numeric, "carats_sold" numeric, "blended_rate" numeric, "invoice_count" bigint, "outstanding_total" numeric, "overdue_total" numeric, "overdue_count" bigint, "stock_value" numeric, "stock_carats" numeric)
    LANGUAGE "sql" STABLE
    AS $$
    with period as (
        select coalesce(sum(amount_total), 0) as amt,
               coalesce(sum(carats_sold), 0)  as ct,
               count(*)                       as n
        from public.v_invoice
        where status = 'POSTED' and invoice_date between p_from and p_to
    ),
    book as (
        select coalesce(sum(outstanding), 0)                                as out_total,
               coalesce(sum(outstanding) filter (where is_overdue), 0)      as od_total,
               count(*) filter (where is_overdue)                           as od_count
        from public.v_invoice
        where status = 'POSTED' and outstanding > 0.01
    ),
    stock as (
        select coalesce(sum(stock_value), 0)                  as val,
               coalesce(sum(greatest(balance_ct, 0)), 0)      as ct
        from public.v_stock_position
    )
    select period.amt,
           period.ct,
           case when period.ct > 0 then period.amt / period.ct else 0 end,
           period.n,
           book.out_total,
           book.od_total,
           book.od_count,
           stock.val,
           stock.ct
    from period, book, stock;
$$;


ALTER FUNCTION "public"."dashboard_summary"("p_from" "date", "p_to" "date") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."delete_imported_stock"() RETURNS integer
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$ declare v_ids bigint[]; v_deleted integer; begin select coalesce(array_agg(distinct ref_id), '{}') into v_ids from public.stock_movement where ref_type = 'stock_import' and ref_id is not null; delete from public.stock_movement where ref_type = 'stock_import'; get diagnostics v_deleted = row_count; if array_length(v_ids, 1) is not null then delete from public.rough_intake where intake_id = any(v_ids); end if; return v_deleted; end; $$;


ALTER FUNCTION "public"."delete_imported_stock"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."handle_new_user"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public', 'pg_temp'
    AS $$
begin
    insert into public.profiles (id, full_name, role)
    values (
        new.id,
        coalesce(new.raw_user_meta_data ->> 'full_name', split_part(new.email, '@', 1)),
        'sales'
    )
    on conflict (id) do nothing;
    return new;
end;
$$;


ALTER FUNCTION "public"."handle_new_user"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."import_stock"("p_as_at" "date", "p_rows" "jsonb", "p_replace" boolean DEFAULT true, "p_batch" "uuid" DEFAULT NULL::"uuid", "p_source" "text" DEFAULT 'excel'::"text") RETURNS "jsonb"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
declare
    v_old_ids bigint[];
    v_deleted integer := 0;
    v_written integer := 0;
    v_batch   uuid    := coalesce(p_batch, gen_random_uuid());
begin
    if p_rows is null or jsonb_typeof(p_rows) <> 'array' then
        raise exception 'import_stock expects an array of rows';
    end if;

    -- Refuse to replace a real dataset with nothing. An empty array is what a
    -- parser returns when it silently failed, and "delete everything" is too
    -- destructive to be reachable by accident. Clearing the import on purpose
    -- is what delete_imported_stock() is for.
    if jsonb_array_length(p_rows) = 0 then
        raise exception 'import_stock was given no rows; use delete_imported_stock() to clear the import';
    end if;

    if p_source is not null and p_source not in ('excel', 'pdf') then
        raise exception 'import_stock does not know the source %', p_source;
    end if;

    if p_replace then
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
    end if;

    -- Parcels and their movements in one statement each, both reading the same
    -- CTE, so a movement without its intake is not expressible here.
    with parcel as (
        insert into public.rough_intake
            (intake_date, grade_id, size_id, weight_ct, price_per_ct, created_by,
             import_batch, import_source)
        select p_as_at,
               (r->>'grade_id')::bigint,
               (r->>'size_id')::bigint,
               (r->>'weight_ct')::numeric,
               (r->>'price_per_ct')::numeric,
               auth.uid(),
               v_batch,
               p_source
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

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'written', v_written, 'batch', v_batch);
end;
$$;


ALTER FUNCTION "public"."import_stock"("p_as_at" "date", "p_rows" "jsonb", "p_replace" boolean, "p_batch" "uuid", "p_source" "text") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."is_manager_or_owner"() RETURNS boolean
    LANGUAGE "sql" STABLE
    AS $$ select public.current_user_role() in ('manager', 'owner') $$;


ALTER FUNCTION "public"."is_manager_or_owner"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."is_owner"() RETURNS boolean
    LANGUAGE "sql" STABLE
    AS $$ select public.current_user_role() = 'owner' $$;


ALTER FUNCTION "public"."is_owner"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."is_staff"() RETURNS boolean
    LANGUAGE "sql" STABLE
    AS $$ select public.current_user_role() in ('sales', 'manager', 'owner') $$;


ALTER FUNCTION "public"."is_staff"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."lockout_minutes"() RETURNS integer
    LANGUAGE "sql" STABLE
    SET "search_path" TO 'public'
    AS $$ select coalesce( (select nullif(value, '')::int from public.app_config where key = 'lockout_minutes'), 15); $$;


ALTER FUNCTION "public"."lockout_minutes"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."login_locked_for"("p_email" "text") RETURNS integer
    LANGUAGE "sql" STABLE SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$ select coalesce( (select greatest(0, ceil(extract(epoch from (locked_until - now())))::int) from public.login_attempt where email = lower(trim(p_email)) and locked_until is not null and locked_until > now()), 0); $$;


ALTER FUNCTION "public"."login_locked_for"("p_email" "text") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."margin_summary"("p_from" "date" DEFAULT NULL::"date", "p_to" "date" DEFAULT NULL::"date") RETURNS TABLE("revenue_total" numeric, "cost_total" numeric, "margin_total" numeric, "margin_pct" numeric, "invoices_costed" bigint, "invoices_total" bigint, "invoices_uncostable" bigint)
    LANGUAGE "sql" STABLE
    AS $$ with scoped as ( select * from public.v_invoice where status = 'POSTED' and (p_from is null or invoice_date >= p_from) and (p_to is null or invoice_date <= p_to) ), costable as ( select * from scoped where invoice_no not like 'MIG-%' ), costed as ( select * from costable where margin is not null ) select round(coalesce(sum(c.amount_total), 0), 2), round(coalesce(sum(c.cost_total), 0), 2), round(coalesce(sum(c.margin), 0), 2), case when coalesce(sum(c.amount_total), 0) > 0 then round(100 * sum(c.margin) / sum(c.amount_total), 2) else 0 end, (select count(*) from costed), (select count(*) from costable), (select count(*) from scoped) - (select count(*) from costable) from costed c; $$;


ALTER FUNCTION "public"."margin_summary"("p_from" "date", "p_to" "date") OWNER TO "postgres";


COMMENT ON FUNCTION "public"."margin_summary"("p_from" "date", "p_to" "date") IS 'Revenue, cost and margin over POSTED invoices that could carry a cost basis, with the count costed against the count costable. Migrated MIG- invoices write no stock movements and can never be costed; they are reported separately as invoices_uncostable rather than dragging the coverage figure down.';



CREATE OR REPLACE FUNCTION "public"."max_login_attempts"() RETURNS integer
    LANGUAGE "sql" STABLE
    SET "search_path" TO 'public'
    AS $$ select greatest(1, least(100, coalesce( (select nullif(value, '')::int from public.app_config where key = 'max_login_attempts'), (select nullif(value, '')::int from public.app_config where key = 'lockout_attempts'), 5))); $$;


ALTER FUNCTION "public"."max_login_attempts"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."negative_stock_policy"() RETURNS "text"
    LANGUAGE "sql" STABLE
    AS $$
select lower(btrim(coalesce(
(select value from public.app_config where key = 'negative_stock'),
(select value from public.app_config where key = 'negative_stock_policy'),
'warn')));
$$;


ALTER FUNCTION "public"."negative_stock_policy"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."next_invoice_no"("p_date" "date" DEFAULT CURRENT_DATE) RETURNS character varying
    LANGUAGE "plpgsql"
    AS $$
declare
    v_year int := extract(year from p_date);
    v_seq  int;
begin
    perform pg_advisory_xact_lock(hashtext('invoice_no' || v_year));

    select coalesce(max(substring(invoice_no from 10)::int), 0) + 1
      into v_seq
      from public.sales_invoice
     where invoice_no like 'INV-' || v_year || '-%';

    return 'INV-' || v_year || '-' || lpad(v_seq::text, 5, '0');
end;
$$;


ALTER FUNCTION "public"."next_invoice_no"("p_date" "date") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."note_login_failure"("p_email" "text") RETURNS integer
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$ declare v_email text := lower(trim(p_email)); v_max int := public.max_login_attempts(); v_fails int; begin insert into public.login_attempt (email, fails, last_fail_at) values (v_email, 1, now()) on conflict (email) do update set fails = case when public.login_attempt.locked_until is not null and public.login_attempt.locked_until <= now() then 1 else public.login_attempt.fails + 1 end, last_fail_at = now(), locked_until = null returning fails into v_fails; if v_fails >= v_max then update public.login_attempt set locked_until = now() + make_interval(mins => public.lockout_minutes()) where email = v_email; end if; return public.login_locked_for(v_email); end; $$;


ALTER FUNCTION "public"."note_login_failure"("p_email" "text") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."post_invoice"("p_invoice_id" bigint, "p_override" boolean DEFAULT false) RETURNS "jsonb"
    LANGUAGE "plpgsql"
    AS $$
declare
    v_status text;
    v_date   date;
    v_policy text;
    v_short  jsonb;
    v_no     varchar(20);
begin
    select status, invoice_date into v_status, v_date
      from public.sales_invoice
     where invoice_id = p_invoice_id;

    if not found then
        raise exception 'Invoice % not found', p_invoice_id using errcode = 'no_data_found';
    end if;

    if v_status = 'POSTED' then
        return jsonb_build_object('ok', true, 'already_posted', true);
    end if;

    if v_status = 'CANCELLED' then
        raise exception 'Invoice % is cancelled and cannot be posted', p_invoice_id;
    end if;

    if not exists (select 1 from public.sales_line where invoice_id = p_invoice_id) then
        raise exception 'Invoice % has no lines', p_invoice_id;
    end if;

    v_policy := public.negative_stock_policy();

    with need as (
        select grade_id, size_id, sum(gross_weight_ct) as out_ct
          from public.sales_line
         where invoice_id = p_invoice_id
         group by grade_id, size_id
    )
    select jsonb_agg(jsonb_build_object(
               'grade_code', sp.grade_code,
               'size_code',  sp.size_code,
               'balance_ct', sp.balance_ct,
               'needed_ct',  n.out_ct))
      into v_short
      from need n
      join public.v_stock_position sp
        on sp.grade_id = n.grade_id and sp.size_id = n.size_id
     where sp.balance_ct < n.out_ct;

    if v_short is not null then
        if v_policy = 'block' then
            raise exception 'Posting would take stock negative: %', v_short::text;
        elsif v_policy = 'warn' and not p_override then
            return jsonb_build_object('ok', false, 'needs_override', true,
                                      'shortfalls', v_short);
        end if;
    end if;

    update public.sales_line l
       set cost_per_ct = nullif(sp.avg_cost, 0),
           cost_basis  = case when nullif(sp.avg_cost, 0) is null
                              then null else 'moving_average' end
      from public.v_stock_position sp
     where l.invoice_id = p_invoice_id
       and sp.grade_id  = l.grade_id
       and sp.size_id   = l.size_id;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select v_date, l.grade_id, l.size_id, 'SALE', l.selection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid()
      from public.sales_line l
     where l.invoice_id = p_invoice_id and l.selection_ct > 0;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select v_date, l.grade_id, l.size_id, 'REJECTION', l.rejection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid()
      from public.sales_line l
     where l.invoice_id = p_invoice_id and l.rejection_ct > 0;

    update public.sales_invoice
       set status     = 'POSTED',
           invoice_no = coalesce(invoice_no, public.next_invoice_no(v_date)),
           updated_by = auth.uid()
     where invoice_id = p_invoice_id;

    select invoice_no into v_no from public.sales_invoice where invoice_id = p_invoice_id;
    return jsonb_build_object('ok', true, 'invoice_no', v_no);
end;
$$;


ALTER FUNCTION "public"."post_invoice"("p_invoice_id" bigint, "p_override" boolean) OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."record_rejection"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric DEFAULT NULL::numeric, "p_date" "date" DEFAULT CURRENT_DATE, "p_client_ref" "uuid" DEFAULT NULL::"uuid", "p_dispositions" "jsonb" DEFAULT '[]'::"jsonb") RETURNS "jsonb"
    LANGUAGE "plpgsql"
    AS $$ declare v_warning text; v_movement_id bigint; v_kept integer := 0; begin if p_weight_ct is null or p_weight_ct <= 0 then raise exception 'Rejection weight must be greater than zero'; end if; v_warning := public.assert_stock(p_grade_id, p_size_id, p_weight_ct); insert into public.stock_movement (movement_date, grade_id, size_id, movement_type, weight_ct, price_per_ct, ref_type, created_by, client_ref) values (p_date, p_grade_id, p_size_id, 'REJECTION', p_weight_ct, p_price_per_ct, 'manual', auth.uid(), p_client_ref) returning movement_id into v_movement_id; if jsonb_typeof(p_dispositions) = 'array' and jsonb_array_length(p_dispositions) > 0 then insert into public.rejection_disposition (movement_id, to_grade_id, weight_ct, note, created_by) select v_movement_id, nullif(d->>'to_grade_id', '')::bigint, (d->>'weight_ct')::numeric, nullif(d->>'note', ''), auth.uid() from jsonb_array_elements(p_dispositions) as d where (d->>'weight_ct')::numeric > 0; get diagnostics v_kept = row_count; end if; return jsonb_build_object('ok', true, 'warning', v_warning, 'movement_id', v_movement_id, 'dispositions', v_kept); end; $$;


ALTER FUNCTION "public"."record_rejection"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric, "p_date" "date", "p_client_ref" "uuid", "p_dispositions" "jsonb") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."replace_imported_sales"("p_payload" "jsonb") RETURNS "jsonb"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$ declare v_old_ids bigint[]; v_deleted integer := 0; v_invoices integer := 0; v_lines integer := 0; v_receipts integer := 0; v_currency bigint; begin if p_payload is null or jsonb_typeof(p_payload->'invoices') <> 'array' then raise exception 'replace_imported_sales expects {"invoices": [...]}'; end if; if jsonb_array_length(p_payload->'invoices') = 0 then raise exception 'replace_imported_sales was given no invoices'; end if; v_currency := (p_payload->>'currency_id')::bigint; if v_currency is null then raise exception 'replace_imported_sales needs a currency_id'; end if; select coalesce(array_agg(invoice_id), '{}') into v_old_ids from public.sales_invoice where invoice_no like 'MIG-%'; if array_length(v_old_ids, 1) is not null then delete from public.receipt where invoice_id = any(v_old_ids); delete from public.sales_line where invoice_id = any(v_old_ids); delete from public.sales_invoice where invoice_id = any(v_old_ids); get diagnostics v_deleted = row_count; end if; with incoming as ( select inv from jsonb_array_elements(p_payload->'invoices') as inv ), written as ( insert into public.sales_invoice (invoice_no, invoice_date, buyer_id, broker_id, broker_pct, terms_days, doc_type, currency_id, status, created_by, updated_by) select inv->>'invoice_no', (inv->>'invoice_date')::date, (inv->>'buyer_id')::bigint, nullif(inv->>'broker_id', '')::bigint, coalesce((inv->>'broker_pct')::numeric, 0), coalesce((inv->>'terms_days')::integer, 0), coalesce(inv->>'doc_type', 'BILL'), v_currency, 'POSTED', auth.uid(), auth.uid() from incoming returning invoice_id, invoice_no ) select count(*) into v_invoices from written; with incoming as ( select inv->>'invoice_no' as no, jsonb_array_elements(coalesce(inv->'lines', '[]'::jsonb)) as ln from jsonb_array_elements(p_payload->'invoices') as inv ) insert into public.sales_line (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct, price_per_ct, ex_rate, less1_pct, less2_pct, remark) select i.invoice_id, (c.ln->>'grade_id')::bigint, (c.ln->>'size_id')::bigint, (c.ln->>'gross_weight_ct')::numeric, (c.ln->>'selection_ct')::numeric, (c.ln->>'price_per_ct')::numeric, coalesce((c.ln->>'ex_rate')::numeric, 1), coalesce((c.ln->>'less1_pct')::numeric, 0), coalesce((c.ln->>'less2_pct')::numeric, 0), nullif(c.ln->>'remark', '') from incoming c join public.sales_invoice i on i.invoice_no = c.no; get diagnostics v_lines = row_count; with incoming as ( select inv->>'invoice_no' as no, (inv->>'received')::numeric as received, (inv->>'invoice_date')::date as on_date from jsonb_array_elements(p_payload->'invoices') as inv ) insert into public.receipt (invoice_id, receipt_date, amount, method, created_by) select i.invoice_id, c.on_date, c.received, 'IMPORTED', auth.uid() from incoming c join public.sales_invoice i on i.invoice_no = c.no where c.received is not null and c.received > 0; get diagnostics v_receipts = row_count; return jsonb_build_object('ok', true, 'deleted', v_deleted, 'invoices', v_invoices, 'lines', v_lines, 'receipts', v_receipts); end; $$;


ALTER FUNCTION "public"."replace_imported_sales"("p_payload" "jsonb") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."replace_imported_stock"("p_as_at" "date", "p_rows" "jsonb") RETURNS "jsonb"
    LANGUAGE "sql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
    select public.import_stock(p_as_at, p_rows, true, null, 'excel');
$$;


ALTER FUNCTION "public"."replace_imported_stock"("p_as_at" "date", "p_rows" "jsonb") OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."sales_line_grade_size_guard"() RETURNS "trigger"
    LANGUAGE "plpgsql"
    SET "search_path" TO 'public'
    AS $$ begin if not exists (select 1 from public.grade_size where grade_id = new.grade_id and size_id = new.size_id) then raise exception 'Grade % does not use size %', (select code from public.grade where grade_id = new.grade_id), (select code from public.size_bucket where size_id = new.size_id) using errcode = 'check_violation'; end if; return new; end; $$;


ALTER FUNCTION "public"."sales_line_grade_size_guard"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."touch_updated_at"() RETURNS "trigger"
    LANGUAGE "plpgsql"
    AS $$
begin
    new.updated_at := now();
    return new;
end;
$$;


ALTER FUNCTION "public"."touch_updated_at"() OWNER TO "postgres";

SET default_tablespace = '';

SET default_table_access_method = "heap";


CREATE TABLE IF NOT EXISTS "public"."app_config" (
    "key" character varying(40) NOT NULL,
    "value" "text" NOT NULL,
    "description" "text",
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL
);

ALTER TABLE ONLY "public"."app_config" FORCE ROW LEVEL SECURITY;


ALTER TABLE "public"."app_config" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."audit_log" (
    "audit_id" bigint NOT NULL,
    "table_name" character varying(40) NOT NULL,
    "record_id" bigint,
    "action" character varying(10) NOT NULL,
    "changed_by" "uuid",
    "changed_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "old_values" "jsonb",
    "new_values" "jsonb",
    "record_uuid" "uuid",
    CONSTRAINT "audit_log_action_check" CHECK ((("action")::"text" = ANY ((ARRAY['INSERT'::character varying, 'UPDATE'::character varying, 'DELETE'::character varying])::"text"[])))
);

ALTER TABLE ONLY "public"."audit_log" FORCE ROW LEVEL SECURITY;


ALTER TABLE "public"."audit_log" OWNER TO "postgres";


COMMENT ON COLUMN "public"."audit_log"."record_uuid" IS 'The key of the audited row when that key is a uuid rather than a bigint. public.profiles is the only such table today; record_id stays null for these rows.';



ALTER TABLE "public"."audit_log" ALTER COLUMN "audit_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."audit_log_audit_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."broker" (
    "broker_id" bigint NOT NULL,
    "name" character varying(120) NOT NULL,
    "default_broker_pct" numeric(5,2) DEFAULT 0 NOT NULL,
    "active" boolean DEFAULT true NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    CONSTRAINT "broker_default_broker_pct_check" CHECK ((("default_broker_pct" >= (0)::numeric) AND ("default_broker_pct" <= (100)::numeric)))
);

ALTER TABLE ONLY "public"."broker" FORCE ROW LEVEL SECURITY;


ALTER TABLE "public"."broker" OWNER TO "postgres";


ALTER TABLE "public"."broker" ALTER COLUMN "broker_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."broker_broker_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."buyer" (
    "buyer_id" bigint NOT NULL,
    "name" character varying(120) NOT NULL,
    "default_terms_days" integer DEFAULT 0 NOT NULL,
    "credit_limit" numeric(14,2),
    "active" boolean DEFAULT true NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    CONSTRAINT "buyer_credit_limit_check" CHECK (("credit_limit" >= (0)::numeric)),
    CONSTRAINT "buyer_default_terms_days_check" CHECK (("default_terms_days" >= 0))
);

ALTER TABLE ONLY "public"."buyer" FORCE ROW LEVEL SECURITY;


ALTER TABLE "public"."buyer" OWNER TO "postgres";


ALTER TABLE "public"."buyer" ALTER COLUMN "buyer_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."buyer_buyer_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."currency" (
    "currency_id" bigint NOT NULL,
    "code" character(3) NOT NULL,
    "latest_rate_to_base" numeric(12,6) DEFAULT 1 NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    CONSTRAINT "currency_latest_rate_to_base_check" CHECK (("latest_rate_to_base" > (0)::numeric))
);

ALTER TABLE ONLY "public"."currency" FORCE ROW LEVEL SECURITY;


ALTER TABLE "public"."currency" OWNER TO "postgres";


ALTER TABLE "public"."currency" ALTER COLUMN "currency_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."currency_currency_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."grade" (
    "grade_id" bigint NOT NULL,
    "code" character varying(16) NOT NULL,
    "display_name" character varying(64) NOT NULL,
    "aliases" character varying(128),
    "sort_order" integer DEFAULT 0 NOT NULL,
    "active" boolean DEFAULT true NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL
);

ALTER TABLE ONLY "public"."grade" FORCE ROW LEVEL SECURITY;


ALTER TABLE "public"."grade" OWNER TO "postgres";


ALTER TABLE "public"."grade" ALTER COLUMN "grade_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."grade_grade_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."grade_size" (
    "grade_id" bigint NOT NULL,
    "size_id" bigint NOT NULL
);


ALTER TABLE "public"."grade_size" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."login_attempt" (
    "email" "text" NOT NULL,
    "fails" integer DEFAULT 0 NOT NULL,
    "last_fail_at" timestamp with time zone,
    "locked_until" timestamp with time zone
);


ALTER TABLE "public"."login_attempt" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."price_list" (
    "price_id" bigint NOT NULL,
    "grade_id" bigint NOT NULL,
    "size_id" bigint NOT NULL,
    "context" character varying(12) NOT NULL,
    "price_per_ct" numeric(12,2) NOT NULL,
    "effective_from" "date" DEFAULT CURRENT_DATE NOT NULL,
    "effective_to" "date",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    CONSTRAINT "price_list_check" CHECK ((("effective_to" IS NULL) OR ("effective_to" >= "effective_from"))),
    CONSTRAINT "price_list_context_check" CHECK ((("context")::"text" = ANY ((ARRAY['STOCK'::character varying, 'REJECTION'::character varying, 'SALE'::character varying])::"text"[]))),
    CONSTRAINT "price_list_price_per_ct_check" CHECK (("price_per_ct" >= (0)::numeric))
);

ALTER TABLE ONLY "public"."price_list" FORCE ROW LEVEL SECURITY;


ALTER TABLE "public"."price_list" OWNER TO "postgres";


ALTER TABLE "public"."price_list" ALTER COLUMN "price_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."price_list_price_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."profiles" (
    "id" "uuid" NOT NULL,
    "full_name" character varying(120) NOT NULL,
    "role" character varying(12) DEFAULT 'sales'::character varying NOT NULL,
    "active" boolean DEFAULT true NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    CONSTRAINT "profiles_role_check" CHECK ((("role")::"text" = ANY ((ARRAY['sales'::character varying, 'manager'::character varying, 'owner'::character varying])::"text"[])))
);

ALTER TABLE ONLY "public"."profiles" FORCE ROW LEVEL SECURITY;


ALTER TABLE "public"."profiles" OWNER TO "postgres";


COMMENT ON TABLE "public"."profiles" IS 'Role per §2.4. The owner Android app requires role in (manager, owner).';



CREATE TABLE IF NOT EXISTS "public"."receipt" (
    "receipt_id" bigint NOT NULL,
    "invoice_id" bigint NOT NULL,
    "receipt_date" "date" NOT NULL,
    "amount" numeric(16,2) NOT NULL,
    "method" character varying(20),
    "reference" character varying(40),
    "created_by" "uuid",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "client_ref" "uuid",
    CONSTRAINT "receipt_amount_check" CHECK (("amount" > (0)::numeric))
);

ALTER TABLE ONLY "public"."receipt" FORCE ROW LEVEL SECURITY;


ALTER TABLE "public"."receipt" OWNER TO "postgres";


ALTER TABLE "public"."receipt" ALTER COLUMN "receipt_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."receipt_receipt_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."rejection_disposition" (
    "disposition_id" bigint NOT NULL,
    "movement_id" bigint NOT NULL,
    "to_grade_id" bigint,
    "weight_ct" numeric(12,4) NOT NULL,
    "note" "text",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "created_by" "uuid",
    CONSTRAINT "rejection_disposition_weight_ct_check" CHECK (("weight_ct" > (0)::numeric))
);


ALTER TABLE "public"."rejection_disposition" OWNER TO "postgres";


CREATE SEQUENCE IF NOT EXISTS "public"."rejection_disposition_disposition_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE "public"."rejection_disposition_disposition_id_seq" OWNER TO "postgres";


ALTER SEQUENCE "public"."rejection_disposition_disposition_id_seq" OWNED BY "public"."rejection_disposition"."disposition_id";



CREATE TABLE IF NOT EXISTS "public"."rough_intake" (
    "intake_id" bigint NOT NULL,
    "intake_date" "date" NOT NULL,
    "grade_id" bigint NOT NULL,
    "size_id" bigint NOT NULL,
    "weight_ct" numeric(12,4) NOT NULL,
    "price_per_ct" numeric(12,2) NOT NULL,
    "created_by" "uuid",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "import_batch" "uuid",
    "import_source" "text",
    CONSTRAINT "rough_intake_price_per_ct_check" CHECK (("price_per_ct" >= (0)::numeric)),
    CONSTRAINT "rough_intake_weight_ct_check" CHECK (("weight_ct" >= (0)::numeric))
);

ALTER TABLE ONLY "public"."rough_intake" FORCE ROW LEVEL SECURITY;


ALTER TABLE "public"."rough_intake" OWNER TO "postgres";


COMMENT ON COLUMN "public"."rough_intake"."import_batch" IS 'Groups the parcels one import wrote, so an appended sheet can be told from the one before it. Null for parcels typed in by hand.';



COMMENT ON COLUMN "public"."rough_intake"."import_source" IS 'How the parcel arrived: excel, pdf, or null for a hand-entered intake.';



ALTER TABLE "public"."rough_intake" ALTER COLUMN "intake_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."rough_intake_intake_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."sales_invoice" (
    "invoice_id" bigint NOT NULL,
    "invoice_no" character varying(20),
    "invoice_date" "date" NOT NULL,
    "buyer_id" bigint NOT NULL,
    "broker_id" bigint,
    "broker_pct" numeric(5,2) DEFAULT 0 NOT NULL,
    "terms_days" integer DEFAULT 0 NOT NULL,
    "doc_type" character varying(12) DEFAULT 'BILL'::character varying NOT NULL,
    "currency_id" bigint NOT NULL,
    "status" character varying(12) DEFAULT 'DRAFT'::character varying NOT NULL,
    "created_by" "uuid",
    "updated_by" "uuid",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "client_ref" "uuid",
    CONSTRAINT "invoice_no_required_when_posted" CHECK (((("status")::"text" <> 'POSTED'::"text") OR ("invoice_no" IS NOT NULL))),
    CONSTRAINT "sales_invoice_broker_pct_check" CHECK ((("broker_pct" >= (0)::numeric) AND ("broker_pct" <= (100)::numeric))),
    CONSTRAINT "sales_invoice_status_check" CHECK ((("status")::"text" = ANY ((ARRAY['DRAFT'::character varying, 'POSTED'::character varying, 'CANCELLED'::character varying])::"text"[]))),
    CONSTRAINT "sales_invoice_terms_days_check" CHECK (("terms_days" >= 0))
);

ALTER TABLE ONLY "public"."sales_invoice" FORCE ROW LEVEL SECURITY;


ALTER TABLE "public"."sales_invoice" OWNER TO "postgres";


COMMENT ON COLUMN "public"."sales_invoice"."invoice_no" IS 'NULL until the invoice is posted. post_invoice() assigns it (docs/03 §2.3).';



COMMENT ON COLUMN "public"."sales_invoice"."client_ref" IS 'Client-generated GUID. Makes an offline replay idempotent (SYNC-001).';



ALTER TABLE "public"."sales_invoice" ALTER COLUMN "invoice_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."sales_invoice_invoice_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."sales_line" (
    "line_id" bigint NOT NULL,
    "invoice_id" bigint NOT NULL,
    "grade_id" bigint NOT NULL,
    "size_id" bigint NOT NULL,
    "gross_weight_ct" numeric(12,4) NOT NULL,
    "selection_ct" numeric(12,4) NOT NULL,
    "rejection_ct" numeric(12,4) GENERATED ALWAYS AS (GREATEST(("gross_weight_ct" - "selection_ct"), (0)::numeric)) STORED,
    "price_per_ct" numeric(12,2) NOT NULL,
    "ex_rate" numeric(12,6) DEFAULT 1 NOT NULL,
    "less1_pct" numeric(5,2) DEFAULT 0 NOT NULL,
    "less2_pct" numeric(5,2) DEFAULT 0 NOT NULL,
    "remark" character varying(255),
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "cost_per_ct" numeric(14,2),
    "cost_basis" "text",
    CONSTRAINT "sales_line_ex_rate_check" CHECK (("ex_rate" > (0)::numeric)),
    CONSTRAINT "sales_line_gross_weight_ct_check" CHECK (("gross_weight_ct" >= (0)::numeric)),
    CONSTRAINT "sales_line_less1_pct_check" CHECK ((("less1_pct" >= (0)::numeric) AND ("less1_pct" <= (100)::numeric))),
    CONSTRAINT "sales_line_less2_pct_check" CHECK ((("less2_pct" >= (0)::numeric) AND ("less2_pct" <= (100)::numeric))),
    CONSTRAINT "sales_line_price_per_ct_check" CHECK (("price_per_ct" >= (0)::numeric)),
    CONSTRAINT "sales_line_selection_ct_check" CHECK (("selection_ct" >= (0)::numeric)),
    CONSTRAINT "selection_within_gross" CHECK (("selection_ct" <= "gross_weight_ct"))
);

ALTER TABLE ONLY "public"."sales_line" FORCE ROW LEVEL SECURITY;


ALTER TABLE "public"."sales_line" OWNER TO "postgres";


COMMENT ON COLUMN "public"."sales_line"."cost_per_ct" IS 'Weighted-average cost per carat at the moment the invoice was posted. Stamped once by post_invoice() and never recomputed. Null means no cost basis exists (migrated invoices, or a grade never taken in).';



COMMENT ON COLUMN "public"."sales_line"."cost_basis" IS 'How cost_per_ct was arrived at, so a later change of method is auditable rather than silent.';



ALTER TABLE "public"."sales_line" ALTER COLUMN "line_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."sales_line_line_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."size_bucket" (
    "size_id" bigint NOT NULL,
    "code" character varying(8) NOT NULL,
    "lower_mm" numeric(5,2),
    "upper_mm" numeric(5,2),
    "sort_order" integer DEFAULT 0 NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL
);

ALTER TABLE ONLY "public"."size_bucket" FORCE ROW LEVEL SECURITY;


ALTER TABLE "public"."size_bucket" OWNER TO "postgres";


ALTER TABLE "public"."size_bucket" ALTER COLUMN "size_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."size_bucket_size_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."stock_movement" (
    "movement_id" bigint NOT NULL,
    "movement_date" "date" NOT NULL,
    "grade_id" bigint NOT NULL,
    "size_id" bigint NOT NULL,
    "movement_type" character varying(16) NOT NULL,
    "weight_ct" numeric(12,4) NOT NULL,
    "price_per_ct" numeric(12,2),
    "ref_type" character varying(16),
    "ref_id" bigint,
    "counterparty_grade_id" bigint,
    "created_by" "uuid",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "client_ref" "uuid",
    "reason" character varying(500),
    CONSTRAINT "adjust_needs_a_reason" CHECK (((("movement_type")::"text" <> 'ADJUST'::"text") OR ("reason" IS NOT NULL))),
    CONSTRAINT "stock_movement_movement_type_check" CHECK ((("movement_type")::"text" = ANY ((ARRAY['INTAKE'::character varying, 'CONVERT_IN'::character varying, 'CONVERT_OUT'::character varying, 'REJECTION'::character varying, 'SALE'::character varying, 'ADJUST'::character varying])::"text"[]))),
    CONSTRAINT "stock_movement_price_per_ct_check" CHECK (("price_per_ct" >= (0)::numeric)),
    CONSTRAINT "stock_movement_weight_ct_check" CHECK ((("weight_ct" >= (0)::numeric) OR (("movement_type")::"text" = 'ADJUST'::"text")))
);

ALTER TABLE ONLY "public"."stock_movement" FORCE ROW LEVEL SECURITY;


ALTER TABLE "public"."stock_movement" OWNER TO "postgres";


COMMENT ON COLUMN "public"."stock_movement"."reason" IS 'Mandatory on ADJUST. An unexplained correction is indistinguishable from an error (BR-INV-1).';



ALTER TABLE "public"."stock_movement" ALTER COLUMN "movement_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."stock_movement_movement_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE OR REPLACE VIEW "public"."v_sales_line" WITH ("security_invoker"='on') AS
 SELECT "l"."line_id",
    "l"."invoice_id",
    "i"."invoice_no",
    "i"."invoice_date",
    "i"."status",
    "i"."buyer_id",
    "i"."created_by",
    "l"."grade_id",
    "g"."code" AS "grade_code",
    "l"."size_id",
    "s"."code" AS "size_code",
    "l"."gross_weight_ct",
    "l"."selection_ct",
    "l"."rejection_ct",
    "l"."price_per_ct",
    "l"."ex_rate",
    "l"."less1_pct",
    "l"."less2_pct",
    "i"."broker_pct",
    "round"(((((("l"."selection_ct" * "l"."price_per_ct") * "l"."ex_rate") * ((1)::numeric - ("l"."less1_pct" / (100)::numeric))) * ((1)::numeric - ("l"."less2_pct" / (100)::numeric))) * ((1)::numeric - ("i"."broker_pct" / (100)::numeric))), 2) AS "amount",
    "round"((((("l"."selection_ct" * "l"."price_per_ct") * "l"."ex_rate") * ((1)::numeric - ("l"."less1_pct" / (100)::numeric))) * ((1)::numeric - ("l"."less2_pct" / (100)::numeric))), 2) AS "amount_pre_broker",
    "l"."remark",
    "l"."updated_at"
   FROM ((("public"."sales_line" "l"
     JOIN "public"."sales_invoice" "i" USING ("invoice_id"))
     JOIN "public"."grade" "g" ON (("g"."grade_id" = "l"."grade_id")))
     JOIN "public"."size_bucket" "s" ON (("s"."size_id" = "l"."size_id")));


ALTER VIEW "public"."v_sales_line" OWNER TO "postgres";


CREATE OR REPLACE VIEW "public"."v_invoice" WITH ("security_invoker"='on') AS
 SELECT "i"."invoice_id",
    "i"."invoice_no",
    "i"."invoice_date",
    "i"."buyer_id",
    "b"."name" AS "buyer_name",
    "b"."credit_limit",
    "i"."broker_id",
    "br"."name" AS "broker_name",
    "i"."broker_pct",
    "i"."terms_days",
    "i"."doc_type",
    "i"."status",
    "i"."created_by",
    "p"."full_name" AS "salesperson",
    COALESCE("t"."amount_total", (0)::numeric) AS "amount_total",
    COALESCE("t"."carats_sold", (0)::numeric) AS "carats_sold",
    COALESCE("r"."received", (0)::numeric) AS "received",
        CASE
            WHEN (("i"."status")::"text" = 'CANCELLED'::"text") THEN (0)::numeric
            ELSE "round"((COALESCE("t"."amount_total", (0)::numeric) - COALESCE("r"."received", (0)::numeric)), 2)
        END AS "outstanding",
        CASE
            WHEN (COALESCE("t"."carats_sold", (0)::numeric) > (0)::numeric) THEN (COALESCE("t"."amount_total", (0)::numeric) / "t"."carats_sold")
            ELSE (0)::numeric
        END AS "blended_rate",
    "round"(((COALESCE("t"."amount_pre_broker", (0)::numeric) * "i"."broker_pct") / (100)::numeric), 2) AS "broker_payable",
    ("i"."invoice_date" + "i"."terms_days") AS "due_date",
    ((CURRENT_DATE > ("i"."invoice_date" + "i"."terms_days")) AND ((COALESCE("t"."amount_total", (0)::numeric) - COALESCE("r"."received", (0)::numeric)) > 0.01) AND (("i"."status")::"text" = 'POSTED'::"text")) AS "is_overdue",
    GREATEST(0, (CURRENT_DATE - ("i"."invoice_date" + "i"."terms_days"))) AS "days_overdue",
    "i"."created_at",
    "i"."updated_at",
        CASE
            WHEN (("c"."lines_total" > 0) AND ("c"."lines_costed" = "c"."lines_total")) THEN "round"("c"."cost_total", 2)
            ELSE NULL::numeric
        END AS "cost_total",
        CASE
            WHEN (("i"."status")::"text" = 'CANCELLED'::"text") THEN NULL::numeric
            WHEN (("c"."lines_total" > 0) AND ("c"."lines_costed" = "c"."lines_total")) THEN "round"((COALESCE("t"."amount_total", (0)::numeric) - "c"."cost_total"), 2)
            ELSE NULL::numeric
        END AS "margin",
        CASE
            WHEN ("c"."lines_total" > 0) THEN "round"((("c"."lines_costed")::numeric / ("c"."lines_total")::numeric), 4)
            ELSE (0)::numeric
        END AS "cost_coverage"
   FROM (((((("public"."sales_invoice" "i"
     JOIN "public"."buyer" "b" ON (("b"."buyer_id" = "i"."buyer_id")))
     LEFT JOIN "public"."broker" "br" ON (("br"."broker_id" = "i"."broker_id")))
     LEFT JOIN "public"."profiles" "p" ON (("p"."id" = "i"."created_by")))
     LEFT JOIN LATERAL ( SELECT "sum"("vl"."amount") AS "amount_total",
            "sum"("vl"."amount_pre_broker") AS "amount_pre_broker",
            "sum"("vl"."selection_ct") AS "carats_sold"
           FROM "public"."v_sales_line" "vl"
          WHERE ("vl"."invoice_id" = "i"."invoice_id")) "t" ON (true))
     LEFT JOIN LATERAL ( SELECT "sum"("rc"."amount") AS "received"
           FROM "public"."receipt" "rc"
          WHERE ("rc"."invoice_id" = "i"."invoice_id")) "r" ON (true))
     LEFT JOIN LATERAL ( SELECT "count"(*) AS "lines_total",
            "count"("sl"."cost_per_ct") AS "lines_costed",
            "sum"(("sl"."cost_per_ct" * "sl"."gross_weight_ct")) AS "cost_total"
           FROM "public"."sales_line" "sl"
          WHERE ("sl"."invoice_id" = "i"."invoice_id")) "c" ON (true));


ALTER VIEW "public"."v_invoice" OWNER TO "postgres";


CREATE OR REPLACE VIEW "public"."v_receivables_ageing" WITH ("security_invoker"='on') AS
 SELECT "invoice_id",
    "invoice_no",
    "buyer_id",
    "buyer_name",
    "outstanding",
    "due_date",
    "days_overdue",
    "is_overdue",
        CASE
            WHEN ("days_overdue" <= 30) THEN '0-30'::"text"
            WHEN ("days_overdue" <= 60) THEN '31-60'::"text"
            WHEN ("days_overdue" <= 90) THEN '61-90'::"text"
            ELSE '90+'::"text"
        END AS "age_bucket"
   FROM "public"."v_invoice" "v"
  WHERE ((("status")::"text" = 'POSTED'::"text") AND ("outstanding" > 0.01));


ALTER VIEW "public"."v_receivables_ageing" OWNER TO "postgres";


CREATE OR REPLACE VIEW "public"."v_reconciliation" WITH ("security_invoker"='on') AS
 SELECT "g"."code" AS "grade_code",
    "s"."code" AS "size_code",
    COALESCE("mv"."sale_ct", (0)::numeric) AS "moved_out_ct",
    COALESCE("sl"."sold_ct", (0)::numeric) AS "sold_on_invoices_ct",
    "round"((COALESCE("mv"."sale_ct", (0)::numeric) - COALESCE("sl"."sold_ct", (0)::numeric)), 4) AS "diff_ct",
    ("abs"((COALESCE("mv"."sale_ct", (0)::numeric) - COALESCE("sl"."sold_ct", (0)::numeric))) < 0.0001) AS "reconciles"
   FROM ((("public"."grade" "g"
     CROSS JOIN "public"."size_bucket" "s")
     LEFT JOIN LATERAL ( SELECT "sum"("m"."weight_ct") AS "sale_ct"
           FROM "public"."stock_movement" "m"
          WHERE (("m"."grade_id" = "g"."grade_id") AND ("m"."size_id" = "s"."size_id") AND (("m"."movement_type")::"text" = 'SALE'::"text"))) "mv" ON (true))
     LEFT JOIN LATERAL ( SELECT "sum"("l"."selection_ct") AS "sold_ct"
           FROM ("public"."sales_line" "l"
             JOIN "public"."sales_invoice" "i" USING ("invoice_id"))
          WHERE (("l"."grade_id" = "g"."grade_id") AND ("l"."size_id" = "s"."size_id") AND (("i"."status")::"text" = 'POSTED'::"text"))) "sl" ON (true));


ALTER VIEW "public"."v_reconciliation" OWNER TO "postgres";


CREATE OR REPLACE VIEW "public"."v_stock_import_batch" AS
 SELECT "import_batch" AS "batch_id",
    COALESCE("import_source", 'excel'::"text") AS "source",
    "min"("intake_date") AS "as_at",
    "max"("intake_id") AS "last_intake_id",
    ("count"(*))::integer AS "parcels",
    "round"("sum"("weight_ct"), 4) AS "carats",
    "round"("sum"(("weight_ct" * "price_per_ct")), 2) AS "value"
   FROM "public"."rough_intake" "i"
  WHERE (EXISTS ( SELECT 1
           FROM "public"."stock_movement" "m"
          WHERE ((("m"."ref_type")::"text" = 'stock_import'::"text") AND ("m"."ref_id" = "i"."intake_id"))))
  GROUP BY "import_batch", COALESCE("import_source", 'excel'::"text");


ALTER VIEW "public"."v_stock_import_batch" OWNER TO "postgres";


CREATE OR REPLACE VIEW "public"."v_stock_movement" WITH ("security_invoker"='on') AS
 SELECT "m"."movement_id",
    "m"."movement_date",
    "g"."code" AS "grade_code",
    "s"."code" AS "size_code",
    "m"."movement_type",
    "m"."weight_ct",
    "m"."price_per_ct",
    "m"."reason",
    "m"."ref_type",
    "m"."ref_id",
    "cg"."code" AS "counterparty_grade_code",
    "m"."created_at",
    "m"."updated_at"
   FROM ((("public"."stock_movement" "m"
     JOIN "public"."grade" "g" ON (("g"."grade_id" = "m"."grade_id")))
     JOIN "public"."size_bucket" "s" ON (("s"."size_id" = "m"."size_id")))
     LEFT JOIN "public"."grade" "cg" ON (("cg"."grade_id" = "m"."counterparty_grade_id")));


ALTER VIEW "public"."v_stock_movement" OWNER TO "postgres";


CREATE OR REPLACE VIEW "public"."v_stock_position" WITH ("security_invoker"='on') AS
 WITH "movement" AS (
         SELECT "m"."grade_id",
            "m"."size_id",
                CASE
                    WHEN (("m"."movement_type")::"text" = ANY ((ARRAY['INTAKE'::character varying, 'CONVERT_IN'::character varying])::"text"[])) THEN "m"."weight_ct"
                    WHEN (("m"."movement_type")::"text" = 'ADJUST'::"text") THEN "m"."weight_ct"
                    ELSE (- "m"."weight_ct")
                END AS "signed_ct",
                CASE
                    WHEN (("m"."movement_type")::"text" = ANY ((ARRAY['INTAKE'::character varying, 'CONVERT_IN'::character varying])::"text"[])) THEN "m"."weight_ct"
                    WHEN ((("m"."movement_type")::"text" = 'ADJUST'::"text") AND ("m"."weight_ct" > (0)::numeric)) THEN "m"."weight_ct"
                    ELSE (0)::numeric
                END AS "inward_ct",
                CASE
                    WHEN (("m"."movement_type")::"text" = ANY ((ARRAY['INTAKE'::character varying, 'CONVERT_IN'::character varying])::"text"[])) THEN ("m"."weight_ct" * COALESCE("m"."price_per_ct", (0)::numeric))
                    WHEN ((("m"."movement_type")::"text" = 'ADJUST'::"text") AND ("m"."weight_ct" > (0)::numeric)) THEN ("m"."weight_ct" * COALESCE("m"."price_per_ct", (0)::numeric))
                    ELSE (0)::numeric
                END AS "inward_value",
                CASE
                    WHEN (("m"."movement_type")::"text" = ANY ((ARRAY['INTAKE'::character varying, 'CONVERT_IN'::character varying])::"text"[])) THEN "m"."movement_date"
                    ELSE NULL::"date"
                END AS "inward_date"
           FROM "public"."stock_movement" "m"
        )
 SELECT "g"."grade_id",
    "g"."code" AS "grade_code",
    "g"."display_name" AS "grade_name",
    "s"."size_id",
    "s"."code" AS "size_code",
    "round"(COALESCE("sum"("mv"."signed_ct"), (0)::numeric), 4) AS "balance_ct",
        CASE
            WHEN (COALESCE("sum"("mv"."inward_ct"), (0)::numeric) > (0)::numeric) THEN ("sum"("mv"."inward_value") / "sum"("mv"."inward_ct"))
            ELSE (0)::numeric
        END AS "avg_cost",
    "round"((GREATEST(COALESCE("sum"("mv"."signed_ct"), (0)::numeric), (0)::numeric) *
        CASE
            WHEN (COALESCE("sum"("mv"."inward_ct"), (0)::numeric) > (0)::numeric) THEN ("sum"("mv"."inward_value") / "sum"("mv"."inward_ct"))
            ELSE (0)::numeric
        END), 2) AS "stock_value",
    "min"("mv"."inward_date") AS "oldest_intake",
    (CURRENT_DATE - "min"("mv"."inward_date")) AS "age_days"
   FROM (("public"."grade" "g"
     CROSS JOIN "public"."size_bucket" "s")
     LEFT JOIN "movement" "mv" ON ((("mv"."grade_id" = "g"."grade_id") AND ("mv"."size_id" = "s"."size_id"))))
  GROUP BY "g"."grade_id", "g"."code", "g"."display_name", "s"."size_id", "s"."code";


ALTER VIEW "public"."v_stock_position" OWNER TO "postgres";


ALTER TABLE ONLY "public"."rejection_disposition" ALTER COLUMN "disposition_id" SET DEFAULT "nextval"('"public"."rejection_disposition_disposition_id_seq"'::"regclass");



ALTER TABLE ONLY "public"."app_config"
    ADD CONSTRAINT "app_config_pkey" PRIMARY KEY ("key");



ALTER TABLE ONLY "public"."audit_log"
    ADD CONSTRAINT "audit_log_pkey" PRIMARY KEY ("audit_id");



ALTER TABLE ONLY "public"."broker"
    ADD CONSTRAINT "broker_name_key" UNIQUE ("name");



ALTER TABLE ONLY "public"."broker"
    ADD CONSTRAINT "broker_pkey" PRIMARY KEY ("broker_id");



ALTER TABLE ONLY "public"."buyer"
    ADD CONSTRAINT "buyer_name_key" UNIQUE ("name");



ALTER TABLE ONLY "public"."buyer"
    ADD CONSTRAINT "buyer_pkey" PRIMARY KEY ("buyer_id");



ALTER TABLE ONLY "public"."currency"
    ADD CONSTRAINT "currency_code_key" UNIQUE ("code");



ALTER TABLE ONLY "public"."currency"
    ADD CONSTRAINT "currency_pkey" PRIMARY KEY ("currency_id");



ALTER TABLE ONLY "public"."grade"
    ADD CONSTRAINT "grade_code_key" UNIQUE ("code");



ALTER TABLE ONLY "public"."grade"
    ADD CONSTRAINT "grade_pkey" PRIMARY KEY ("grade_id");



ALTER TABLE ONLY "public"."grade_size"
    ADD CONSTRAINT "grade_size_pkey" PRIMARY KEY ("grade_id", "size_id");



ALTER TABLE ONLY "public"."login_attempt"
    ADD CONSTRAINT "login_attempt_pkey" PRIMARY KEY ("email");



ALTER TABLE ONLY "public"."price_list"
    ADD CONSTRAINT "price_list_pkey" PRIMARY KEY ("price_id");



ALTER TABLE ONLY "public"."profiles"
    ADD CONSTRAINT "profiles_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."receipt"
    ADD CONSTRAINT "receipt_pkey" PRIMARY KEY ("receipt_id");



ALTER TABLE ONLY "public"."rejection_disposition"
    ADD CONSTRAINT "rejection_disposition_pkey" PRIMARY KEY ("disposition_id");



ALTER TABLE ONLY "public"."rough_intake"
    ADD CONSTRAINT "rough_intake_pkey" PRIMARY KEY ("intake_id");



ALTER TABLE ONLY "public"."sales_invoice"
    ADD CONSTRAINT "sales_invoice_invoice_no_key" UNIQUE ("invoice_no");



ALTER TABLE ONLY "public"."sales_invoice"
    ADD CONSTRAINT "sales_invoice_pkey" PRIMARY KEY ("invoice_id");



ALTER TABLE ONLY "public"."sales_line"
    ADD CONSTRAINT "sales_line_pkey" PRIMARY KEY ("line_id");



ALTER TABLE ONLY "public"."size_bucket"
    ADD CONSTRAINT "size_bucket_code_key" UNIQUE ("code");



ALTER TABLE ONLY "public"."size_bucket"
    ADD CONSTRAINT "size_bucket_pkey" PRIMARY KEY ("size_id");



ALTER TABLE ONLY "public"."stock_movement"
    ADD CONSTRAINT "stock_movement_pkey" PRIMARY KEY ("movement_id");



CREATE INDEX "audit_log_date_idx" ON "public"."audit_log" USING "btree" ("changed_at" DESC);



CREATE INDEX "audit_log_record_uuid_idx" ON "public"."audit_log" USING "btree" ("record_uuid", "changed_at" DESC) WHERE ("record_uuid" IS NOT NULL);



CREATE INDEX "audit_log_table_idx" ON "public"."audit_log" USING "btree" ("table_name", "record_id");



CREATE INDEX "audit_log_user_idx" ON "public"."audit_log" USING "btree" ("changed_by");



CREATE UNIQUE INDEX "price_list_current_uq" ON "public"."price_list" USING "btree" ("grade_id", "size_id", "context", "effective_from") WHERE ("effective_to" IS NULL);



CREATE UNIQUE INDEX "receipt_client_ref_uq" ON "public"."receipt" USING "btree" ("client_ref") WHERE ("client_ref" IS NOT NULL);



CREATE INDEX "receipt_invoice_idx" ON "public"."receipt" USING "btree" ("invoice_id");



CREATE INDEX "rejection_disposition_movement" ON "public"."rejection_disposition" USING "btree" ("movement_id");



CREATE INDEX "rough_intake_import_batch_idx" ON "public"."rough_intake" USING "btree" ("import_batch") WHERE ("import_batch" IS NOT NULL);



CREATE INDEX "sales_invoice_buyer_idx" ON "public"."sales_invoice" USING "btree" ("buyer_id");



CREATE UNIQUE INDEX "sales_invoice_client_ref_uq" ON "public"."sales_invoice" USING "btree" ("client_ref") WHERE ("client_ref" IS NOT NULL);



CREATE INDEX "sales_invoice_date_idx" ON "public"."sales_invoice" USING "btree" ("invoice_date" DESC);



CREATE INDEX "sales_invoice_status_idx" ON "public"."sales_invoice" USING "btree" ("status");



CREATE INDEX "sales_invoice_sync_idx" ON "public"."sales_invoice" USING "btree" ("updated_at");



CREATE INDEX "sales_line_grade_idx" ON "public"."sales_line" USING "btree" ("grade_id", "size_id");



CREATE INDEX "sales_line_invoice_idx" ON "public"."sales_line" USING "btree" ("invoice_id");



CREATE UNIQUE INDEX "stock_movement_client_ref_uq" ON "public"."stock_movement" USING "btree" ("client_ref") WHERE ("client_ref" IS NOT NULL);



CREATE INDEX "stock_movement_date_idx" ON "public"."stock_movement" USING "btree" ("movement_date" DESC);



CREATE INDEX "stock_movement_grade_idx" ON "public"."stock_movement" USING "btree" ("grade_id", "size_id");



CREATE INDEX "stock_movement_ref_idx" ON "public"."stock_movement" USING "btree" ("ref_type", "ref_id");



CREATE INDEX "stock_movement_sync_idx" ON "public"."stock_movement" USING "btree" ("updated_at");



CREATE OR REPLACE TRIGGER "app_config_touch" BEFORE UPDATE ON "public"."app_config" FOR EACH ROW EXECUTE FUNCTION "public"."touch_updated_at"();



CREATE OR REPLACE TRIGGER "broker_touch" BEFORE UPDATE ON "public"."broker" FOR EACH ROW EXECUTE FUNCTION "public"."touch_updated_at"();



CREATE OR REPLACE TRIGGER "buyer_audit" AFTER INSERT OR DELETE OR UPDATE ON "public"."buyer" FOR EACH ROW EXECUTE FUNCTION "public"."audit_change"('buyer_id');



CREATE OR REPLACE TRIGGER "buyer_touch" BEFORE UPDATE ON "public"."buyer" FOR EACH ROW EXECUTE FUNCTION "public"."touch_updated_at"();



CREATE OR REPLACE TRIGGER "currency_touch" BEFORE UPDATE ON "public"."currency" FOR EACH ROW EXECUTE FUNCTION "public"."touch_updated_at"();



CREATE OR REPLACE TRIGGER "grade_touch" BEFORE UPDATE ON "public"."grade" FOR EACH ROW EXECUTE FUNCTION "public"."touch_updated_at"();



CREATE OR REPLACE TRIGGER "price_list_audit" AFTER INSERT OR DELETE OR UPDATE ON "public"."price_list" FOR EACH ROW EXECUTE FUNCTION "public"."audit_change"('price_id');



CREATE OR REPLACE TRIGGER "price_list_touch" BEFORE UPDATE ON "public"."price_list" FOR EACH ROW EXECUTE FUNCTION "public"."touch_updated_at"();



CREATE OR REPLACE TRIGGER "profiles_touch" BEFORE UPDATE ON "public"."profiles" FOR EACH ROW EXECUTE FUNCTION "public"."touch_updated_at"();



CREATE OR REPLACE TRIGGER "receipt_audit" AFTER INSERT OR DELETE OR UPDATE ON "public"."receipt" FOR EACH ROW EXECUTE FUNCTION "public"."audit_change"('receipt_id');



CREATE OR REPLACE TRIGGER "receipt_touch" BEFORE UPDATE ON "public"."receipt" FOR EACH ROW EXECUTE FUNCTION "public"."touch_updated_at"();



CREATE OR REPLACE TRIGGER "rough_intake_touch" BEFORE UPDATE ON "public"."rough_intake" FOR EACH ROW EXECUTE FUNCTION "public"."touch_updated_at"();



CREATE OR REPLACE TRIGGER "sales_invoice_audit" AFTER INSERT OR DELETE OR UPDATE ON "public"."sales_invoice" FOR EACH ROW EXECUTE FUNCTION "public"."audit_change"('invoice_id');



CREATE OR REPLACE TRIGGER "sales_invoice_touch" BEFORE UPDATE ON "public"."sales_invoice" FOR EACH ROW EXECUTE FUNCTION "public"."touch_updated_at"();



CREATE OR REPLACE TRIGGER "sales_line_audit" AFTER INSERT OR DELETE OR UPDATE ON "public"."sales_line" FOR EACH ROW EXECUTE FUNCTION "public"."audit_change"('line_id');



CREATE OR REPLACE TRIGGER "sales_line_grade_size" BEFORE INSERT OR UPDATE OF "grade_id", "size_id" ON "public"."sales_line" FOR EACH ROW EXECUTE FUNCTION "public"."sales_line_grade_size_guard"();



CREATE OR REPLACE TRIGGER "sales_line_touch" BEFORE UPDATE ON "public"."sales_line" FOR EACH ROW EXECUTE FUNCTION "public"."touch_updated_at"();



CREATE OR REPLACE TRIGGER "size_bucket_touch" BEFORE UPDATE ON "public"."size_bucket" FOR EACH ROW EXECUTE FUNCTION "public"."touch_updated_at"();



CREATE OR REPLACE TRIGGER "stock_movement_audit" AFTER INSERT OR DELETE OR UPDATE ON "public"."stock_movement" FOR EACH ROW EXECUTE FUNCTION "public"."audit_change"('movement_id');



CREATE OR REPLACE TRIGGER "stock_movement_touch" BEFORE UPDATE ON "public"."stock_movement" FOR EACH ROW EXECUTE FUNCTION "public"."touch_updated_at"();



ALTER TABLE ONLY "public"."grade_size"
    ADD CONSTRAINT "grade_size_grade_id_fkey" FOREIGN KEY ("grade_id") REFERENCES "public"."grade"("grade_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."grade_size"
    ADD CONSTRAINT "grade_size_size_id_fkey" FOREIGN KEY ("size_id") REFERENCES "public"."size_bucket"("size_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."price_list"
    ADD CONSTRAINT "price_list_grade_id_fkey" FOREIGN KEY ("grade_id") REFERENCES "public"."grade"("grade_id");



ALTER TABLE ONLY "public"."price_list"
    ADD CONSTRAINT "price_list_size_id_fkey" FOREIGN KEY ("size_id") REFERENCES "public"."size_bucket"("size_id");



ALTER TABLE ONLY "public"."profiles"
    ADD CONSTRAINT "profiles_id_fkey" FOREIGN KEY ("id") REFERENCES "auth"."users"("id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."receipt"
    ADD CONSTRAINT "receipt_created_by_fkey" FOREIGN KEY ("created_by") REFERENCES "public"."profiles"("id");



ALTER TABLE ONLY "public"."receipt"
    ADD CONSTRAINT "receipt_invoice_id_fkey" FOREIGN KEY ("invoice_id") REFERENCES "public"."sales_invoice"("invoice_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."rejection_disposition"
    ADD CONSTRAINT "rejection_disposition_movement_id_fkey" FOREIGN KEY ("movement_id") REFERENCES "public"."stock_movement"("movement_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."rejection_disposition"
    ADD CONSTRAINT "rejection_disposition_to_grade_id_fkey" FOREIGN KEY ("to_grade_id") REFERENCES "public"."grade"("grade_id");



ALTER TABLE ONLY "public"."rough_intake"
    ADD CONSTRAINT "rough_intake_created_by_fkey" FOREIGN KEY ("created_by") REFERENCES "public"."profiles"("id");



ALTER TABLE ONLY "public"."rough_intake"
    ADD CONSTRAINT "rough_intake_grade_id_fkey" FOREIGN KEY ("grade_id") REFERENCES "public"."grade"("grade_id");



ALTER TABLE ONLY "public"."rough_intake"
    ADD CONSTRAINT "rough_intake_size_id_fkey" FOREIGN KEY ("size_id") REFERENCES "public"."size_bucket"("size_id");



ALTER TABLE ONLY "public"."sales_invoice"
    ADD CONSTRAINT "sales_invoice_broker_id_fkey" FOREIGN KEY ("broker_id") REFERENCES "public"."broker"("broker_id");



ALTER TABLE ONLY "public"."sales_invoice"
    ADD CONSTRAINT "sales_invoice_buyer_id_fkey" FOREIGN KEY ("buyer_id") REFERENCES "public"."buyer"("buyer_id");



ALTER TABLE ONLY "public"."sales_invoice"
    ADD CONSTRAINT "sales_invoice_created_by_fkey" FOREIGN KEY ("created_by") REFERENCES "public"."profiles"("id");



ALTER TABLE ONLY "public"."sales_invoice"
    ADD CONSTRAINT "sales_invoice_currency_id_fkey" FOREIGN KEY ("currency_id") REFERENCES "public"."currency"("currency_id");



ALTER TABLE ONLY "public"."sales_invoice"
    ADD CONSTRAINT "sales_invoice_updated_by_fkey" FOREIGN KEY ("updated_by") REFERENCES "public"."profiles"("id");



ALTER TABLE ONLY "public"."sales_line"
    ADD CONSTRAINT "sales_line_grade_id_fkey" FOREIGN KEY ("grade_id") REFERENCES "public"."grade"("grade_id");



ALTER TABLE ONLY "public"."sales_line"
    ADD CONSTRAINT "sales_line_invoice_id_fkey" FOREIGN KEY ("invoice_id") REFERENCES "public"."sales_invoice"("invoice_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."sales_line"
    ADD CONSTRAINT "sales_line_size_id_fkey" FOREIGN KEY ("size_id") REFERENCES "public"."size_bucket"("size_id");



ALTER TABLE ONLY "public"."stock_movement"
    ADD CONSTRAINT "stock_movement_counterparty_grade_id_fkey" FOREIGN KEY ("counterparty_grade_id") REFERENCES "public"."grade"("grade_id");



ALTER TABLE ONLY "public"."stock_movement"
    ADD CONSTRAINT "stock_movement_created_by_fkey" FOREIGN KEY ("created_by") REFERENCES "public"."profiles"("id");



ALTER TABLE ONLY "public"."stock_movement"
    ADD CONSTRAINT "stock_movement_grade_id_fkey" FOREIGN KEY ("grade_id") REFERENCES "public"."grade"("grade_id");



ALTER TABLE ONLY "public"."stock_movement"
    ADD CONSTRAINT "stock_movement_size_id_fkey" FOREIGN KEY ("size_id") REFERENCES "public"."size_bucket"("size_id");



ALTER TABLE "public"."app_config" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."audit_log" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "audit_read" ON "public"."audit_log" FOR SELECT USING ("public"."is_manager_or_owner"());



ALTER TABLE "public"."broker" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "broker_read" ON "public"."broker" FOR SELECT USING ("public"."is_staff"());



CREATE POLICY "broker_write" ON "public"."broker" USING ("public"."is_manager_or_owner"()) WITH CHECK ("public"."is_manager_or_owner"());



ALTER TABLE "public"."buyer" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "buyer_read" ON "public"."buyer" FOR SELECT USING ("public"."is_staff"());



CREATE POLICY "buyer_write" ON "public"."buyer" USING ("public"."is_manager_or_owner"()) WITH CHECK ("public"."is_manager_or_owner"());



CREATE POLICY "config_read" ON "public"."app_config" FOR SELECT USING ("public"."is_staff"());



CREATE POLICY "config_write" ON "public"."app_config" USING ("public"."is_owner"()) WITH CHECK ("public"."is_owner"());



ALTER TABLE "public"."currency" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "currency_read" ON "public"."currency" FOR SELECT USING ("public"."is_staff"());



CREATE POLICY "currency_write" ON "public"."currency" USING ("public"."is_manager_or_owner"()) WITH CHECK ("public"."is_manager_or_owner"());



ALTER TABLE "public"."grade" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "grade_read" ON "public"."grade" FOR SELECT USING ("public"."is_staff"());



ALTER TABLE "public"."grade_size" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "grade_size_read" ON "public"."grade_size" FOR SELECT TO "authenticated" USING (true);



CREATE POLICY "grade_write" ON "public"."grade" USING ("public"."is_manager_or_owner"()) WITH CHECK ("public"."is_manager_or_owner"());



CREATE POLICY "intake_read" ON "public"."rough_intake" FOR SELECT USING ("public"."is_staff"());



CREATE POLICY "intake_write" ON "public"."rough_intake" USING ("public"."is_manager_or_owner"()) WITH CHECK ("public"."is_manager_or_owner"());



CREATE POLICY "invoice_delete" ON "public"."sales_invoice" FOR DELETE USING ("public"."is_manager_or_owner"());



CREATE POLICY "invoice_insert" ON "public"."sales_invoice" FOR INSERT WITH CHECK (("public"."is_staff"() AND ("created_by" = "auth"."uid"())));



CREATE POLICY "invoice_read" ON "public"."sales_invoice" FOR SELECT USING (("public"."is_manager_or_owner"() OR ("created_by" = "auth"."uid"())));



CREATE POLICY "invoice_update" ON "public"."sales_invoice" FOR UPDATE USING (("public"."is_manager_or_owner"() OR (("created_by" = "auth"."uid"()) AND (("status")::"text" = 'DRAFT'::"text")))) WITH CHECK (("public"."is_manager_or_owner"() OR (("created_by" = "auth"."uid"()) AND (("status")::"text" = ANY ((ARRAY['DRAFT'::character varying, 'POSTED'::character varying])::"text"[])))));



CREATE POLICY "line_read" ON "public"."sales_line" FOR SELECT USING ((EXISTS ( SELECT 1
   FROM "public"."sales_invoice" "i"
  WHERE (("i"."invoice_id" = "sales_line"."invoice_id") AND ("public"."is_manager_or_owner"() OR ("i"."created_by" = "auth"."uid"()))))));



CREATE POLICY "line_write" ON "public"."sales_line" USING ((EXISTS ( SELECT 1
   FROM "public"."sales_invoice" "i"
  WHERE (("i"."invoice_id" = "sales_line"."invoice_id") AND ("public"."is_manager_or_owner"() OR (("i"."created_by" = "auth"."uid"()) AND (("i"."status")::"text" = 'DRAFT'::"text"))))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM "public"."sales_invoice" "i"
  WHERE (("i"."invoice_id" = "sales_line"."invoice_id") AND ("public"."is_manager_or_owner"() OR (("i"."created_by" = "auth"."uid"()) AND (("i"."status")::"text" = 'DRAFT'::"text")))))));



ALTER TABLE "public"."login_attempt" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "movement_insert" ON "public"."stock_movement" FOR INSERT WITH CHECK ("public"."is_staff"());



CREATE POLICY "movement_read" ON "public"."stock_movement" FOR SELECT USING ("public"."is_staff"());



ALTER TABLE "public"."price_list" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "price_list_read" ON "public"."price_list" FOR SELECT USING ("public"."is_staff"());



CREATE POLICY "price_list_write" ON "public"."price_list" USING ("public"."is_manager_or_owner"()) WITH CHECK ("public"."is_manager_or_owner"());



ALTER TABLE "public"."profiles" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "profiles_owner_writes" ON "public"."profiles" USING ("public"."is_owner"()) WITH CHECK ("public"."is_owner"());



CREATE POLICY "profiles_read_own" ON "public"."profiles" FOR SELECT USING ((("id" = "auth"."uid"()) OR "public"."is_manager_or_owner"()));



ALTER TABLE "public"."receipt" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "receipt_amend" ON "public"."receipt" FOR UPDATE USING ("public"."is_manager_or_owner"()) WITH CHECK ("public"."is_manager_or_owner"());



CREATE POLICY "receipt_delete" ON "public"."receipt" FOR DELETE USING ("public"."is_manager_or_owner"());



CREATE POLICY "receipt_insert" ON "public"."receipt" FOR INSERT WITH CHECK ("public"."is_staff"());



CREATE POLICY "receipt_read" ON "public"."receipt" FOR SELECT USING ((EXISTS ( SELECT 1
   FROM "public"."sales_invoice" "i"
  WHERE (("i"."invoice_id" = "receipt"."invoice_id") AND ("public"."is_manager_or_owner"() OR ("i"."created_by" = "auth"."uid"()))))));



ALTER TABLE "public"."rejection_disposition" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "rejection_disposition_read" ON "public"."rejection_disposition" FOR SELECT TO "authenticated" USING (true);



CREATE POLICY "rejection_disposition_write" ON "public"."rejection_disposition" FOR INSERT TO "authenticated" WITH CHECK (true);



ALTER TABLE "public"."rough_intake" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."sales_invoice" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."sales_line" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."size_bucket" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "size_bucket_read" ON "public"."size_bucket" FOR SELECT USING ("public"."is_staff"());



CREATE POLICY "size_bucket_write" ON "public"."size_bucket" USING ("public"."is_manager_or_owner"()) WITH CHECK ("public"."is_manager_or_owner"());



ALTER TABLE "public"."stock_movement" ENABLE ROW LEVEL SECURITY;




ALTER PUBLICATION "supabase_realtime" OWNER TO "postgres";


GRANT USAGE ON SCHEMA "public" TO "postgres";
GRANT USAGE ON SCHEMA "public" TO "authenticated";
GRANT USAGE ON SCHEMA "public" TO "service_role";






















































































































































REVOKE ALL ON FUNCTION "public"."add_grade"("p_code" "text", "p_display_name" "text") FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."add_grade"("p_code" "text", "p_display_name" "text") TO "anon";
GRANT ALL ON FUNCTION "public"."add_grade"("p_code" "text", "p_display_name" "text") TO "authenticated";
GRANT ALL ON FUNCTION "public"."add_grade"("p_code" "text", "p_display_name" "text") TO "service_role";



GRANT ALL ON FUNCTION "public"."adjust_stock"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_reason" "text", "p_date" "date", "p_client_ref" "uuid") TO "anon";
GRANT ALL ON FUNCTION "public"."adjust_stock"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_reason" "text", "p_date" "date", "p_client_ref" "uuid") TO "authenticated";
GRANT ALL ON FUNCTION "public"."adjust_stock"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_reason" "text", "p_date" "date", "p_client_ref" "uuid") TO "service_role";



REVOKE ALL ON FUNCTION "public"."adjust_stock_at_cost"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric, "p_reason" "text", "p_date" "date", "p_client_ref" "uuid") FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."adjust_stock_at_cost"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric, "p_reason" "text", "p_date" "date", "p_client_ref" "uuid") TO "anon";
GRANT ALL ON FUNCTION "public"."adjust_stock_at_cost"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric, "p_reason" "text", "p_date" "date", "p_client_ref" "uuid") TO "authenticated";
GRANT ALL ON FUNCTION "public"."adjust_stock_at_cost"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric, "p_reason" "text", "p_date" "date", "p_client_ref" "uuid") TO "service_role";



GRANT ALL ON FUNCTION "public"."assert_stock"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric) TO "anon";
GRANT ALL ON FUNCTION "public"."assert_stock"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric) TO "authenticated";
GRANT ALL ON FUNCTION "public"."assert_stock"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric) TO "service_role";



GRANT ALL ON FUNCTION "public"."audit_change"() TO "anon";
GRANT ALL ON FUNCTION "public"."audit_change"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."audit_change"() TO "service_role";



GRANT ALL ON FUNCTION "public"."cancel_invoice"("p_invoice_id" bigint, "p_reason" "text") TO "anon";
GRANT ALL ON FUNCTION "public"."cancel_invoice"("p_invoice_id" bigint, "p_reason" "text") TO "authenticated";
GRANT ALL ON FUNCTION "public"."cancel_invoice"("p_invoice_id" bigint, "p_reason" "text") TO "service_role";



GRANT ALL ON FUNCTION "public"."clear_login_failures"("p_email" "text") TO "anon";
GRANT ALL ON FUNCTION "public"."clear_login_failures"("p_email" "text") TO "authenticated";
GRANT ALL ON FUNCTION "public"."clear_login_failures"("p_email" "text") TO "service_role";



GRANT ALL ON FUNCTION "public"."convert_stock"("p_from_grade_id" bigint, "p_from_size_id" bigint, "p_to_grade_id" bigint, "p_to_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric, "p_date" "date", "p_client_ref" "uuid") TO "anon";
GRANT ALL ON FUNCTION "public"."convert_stock"("p_from_grade_id" bigint, "p_from_size_id" bigint, "p_to_grade_id" bigint, "p_to_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric, "p_date" "date", "p_client_ref" "uuid") TO "authenticated";
GRANT ALL ON FUNCTION "public"."convert_stock"("p_from_grade_id" bigint, "p_from_size_id" bigint, "p_to_grade_id" bigint, "p_to_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric, "p_date" "date", "p_client_ref" "uuid") TO "service_role";



GRANT ALL ON FUNCTION "public"."current_user_role"() TO "anon";
GRANT ALL ON FUNCTION "public"."current_user_role"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."current_user_role"() TO "service_role";



GRANT ALL ON FUNCTION "public"."dashboard_summary"("p_from" "date", "p_to" "date") TO "anon";
GRANT ALL ON FUNCTION "public"."dashboard_summary"("p_from" "date", "p_to" "date") TO "authenticated";
GRANT ALL ON FUNCTION "public"."dashboard_summary"("p_from" "date", "p_to" "date") TO "service_role";



REVOKE ALL ON FUNCTION "public"."delete_imported_stock"() FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."delete_imported_stock"() TO "anon";
GRANT ALL ON FUNCTION "public"."delete_imported_stock"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."delete_imported_stock"() TO "service_role";



GRANT ALL ON FUNCTION "public"."handle_new_user"() TO "anon";
GRANT ALL ON FUNCTION "public"."handle_new_user"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."handle_new_user"() TO "service_role";



REVOKE ALL ON FUNCTION "public"."import_stock"("p_as_at" "date", "p_rows" "jsonb", "p_replace" boolean, "p_batch" "uuid", "p_source" "text") FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."import_stock"("p_as_at" "date", "p_rows" "jsonb", "p_replace" boolean, "p_batch" "uuid", "p_source" "text") TO "anon";
GRANT ALL ON FUNCTION "public"."import_stock"("p_as_at" "date", "p_rows" "jsonb", "p_replace" boolean, "p_batch" "uuid", "p_source" "text") TO "authenticated";
GRANT ALL ON FUNCTION "public"."import_stock"("p_as_at" "date", "p_rows" "jsonb", "p_replace" boolean, "p_batch" "uuid", "p_source" "text") TO "service_role";



GRANT ALL ON FUNCTION "public"."is_manager_or_owner"() TO "anon";
GRANT ALL ON FUNCTION "public"."is_manager_or_owner"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."is_manager_or_owner"() TO "service_role";



GRANT ALL ON FUNCTION "public"."is_owner"() TO "anon";
GRANT ALL ON FUNCTION "public"."is_owner"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."is_owner"() TO "service_role";



GRANT ALL ON FUNCTION "public"."is_staff"() TO "anon";
GRANT ALL ON FUNCTION "public"."is_staff"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."is_staff"() TO "service_role";



GRANT ALL ON FUNCTION "public"."lockout_minutes"() TO "anon";
GRANT ALL ON FUNCTION "public"."lockout_minutes"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."lockout_minutes"() TO "service_role";



GRANT ALL ON FUNCTION "public"."login_locked_for"("p_email" "text") TO "anon";
GRANT ALL ON FUNCTION "public"."login_locked_for"("p_email" "text") TO "authenticated";
GRANT ALL ON FUNCTION "public"."login_locked_for"("p_email" "text") TO "service_role";



REVOKE ALL ON FUNCTION "public"."margin_summary"("p_from" "date", "p_to" "date") FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."margin_summary"("p_from" "date", "p_to" "date") TO "anon";
GRANT ALL ON FUNCTION "public"."margin_summary"("p_from" "date", "p_to" "date") TO "authenticated";
GRANT ALL ON FUNCTION "public"."margin_summary"("p_from" "date", "p_to" "date") TO "service_role";



GRANT ALL ON FUNCTION "public"."max_login_attempts"() TO "anon";
GRANT ALL ON FUNCTION "public"."max_login_attempts"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."max_login_attempts"() TO "service_role";



GRANT ALL ON FUNCTION "public"."negative_stock_policy"() TO "anon";
GRANT ALL ON FUNCTION "public"."negative_stock_policy"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."negative_stock_policy"() TO "service_role";



GRANT ALL ON FUNCTION "public"."next_invoice_no"("p_date" "date") TO "anon";
GRANT ALL ON FUNCTION "public"."next_invoice_no"("p_date" "date") TO "authenticated";
GRANT ALL ON FUNCTION "public"."next_invoice_no"("p_date" "date") TO "service_role";



GRANT ALL ON FUNCTION "public"."note_login_failure"("p_email" "text") TO "anon";
GRANT ALL ON FUNCTION "public"."note_login_failure"("p_email" "text") TO "authenticated";
GRANT ALL ON FUNCTION "public"."note_login_failure"("p_email" "text") TO "service_role";



GRANT ALL ON FUNCTION "public"."post_invoice"("p_invoice_id" bigint, "p_override" boolean) TO "anon";
GRANT ALL ON FUNCTION "public"."post_invoice"("p_invoice_id" bigint, "p_override" boolean) TO "authenticated";
GRANT ALL ON FUNCTION "public"."post_invoice"("p_invoice_id" bigint, "p_override" boolean) TO "service_role";



GRANT ALL ON FUNCTION "public"."record_rejection"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric, "p_date" "date", "p_client_ref" "uuid", "p_dispositions" "jsonb") TO "anon";
GRANT ALL ON FUNCTION "public"."record_rejection"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric, "p_date" "date", "p_client_ref" "uuid", "p_dispositions" "jsonb") TO "authenticated";
GRANT ALL ON FUNCTION "public"."record_rejection"("p_grade_id" bigint, "p_size_id" bigint, "p_weight_ct" numeric, "p_price_per_ct" numeric, "p_date" "date", "p_client_ref" "uuid", "p_dispositions" "jsonb") TO "service_role";



GRANT ALL ON FUNCTION "public"."replace_imported_sales"("p_payload" "jsonb") TO "anon";
GRANT ALL ON FUNCTION "public"."replace_imported_sales"("p_payload" "jsonb") TO "authenticated";
GRANT ALL ON FUNCTION "public"."replace_imported_sales"("p_payload" "jsonb") TO "service_role";



REVOKE ALL ON FUNCTION "public"."replace_imported_stock"("p_as_at" "date", "p_rows" "jsonb") FROM PUBLIC;
GRANT ALL ON FUNCTION "public"."replace_imported_stock"("p_as_at" "date", "p_rows" "jsonb") TO "anon";
GRANT ALL ON FUNCTION "public"."replace_imported_stock"("p_as_at" "date", "p_rows" "jsonb") TO "authenticated";
GRANT ALL ON FUNCTION "public"."replace_imported_stock"("p_as_at" "date", "p_rows" "jsonb") TO "service_role";



GRANT ALL ON FUNCTION "public"."sales_line_grade_size_guard"() TO "anon";
GRANT ALL ON FUNCTION "public"."sales_line_grade_size_guard"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."sales_line_grade_size_guard"() TO "service_role";



GRANT ALL ON FUNCTION "public"."touch_updated_at"() TO "anon";
GRANT ALL ON FUNCTION "public"."touch_updated_at"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."touch_updated_at"() TO "service_role";


















GRANT ALL ON TABLE "public"."app_config" TO "authenticated";
GRANT ALL ON TABLE "public"."app_config" TO "service_role";



GRANT SELECT,REFERENCES,TRIGGER,TRUNCATE,MAINTAIN ON TABLE "public"."audit_log" TO "authenticated";
GRANT ALL ON TABLE "public"."audit_log" TO "service_role";



GRANT ALL ON SEQUENCE "public"."audit_log_audit_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."audit_log_audit_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."audit_log_audit_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."broker" TO "authenticated";
GRANT ALL ON TABLE "public"."broker" TO "service_role";



GRANT ALL ON SEQUENCE "public"."broker_broker_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."broker_broker_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."buyer" TO "authenticated";
GRANT ALL ON TABLE "public"."buyer" TO "service_role";



GRANT ALL ON SEQUENCE "public"."buyer_buyer_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."buyer_buyer_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."currency" TO "authenticated";
GRANT ALL ON TABLE "public"."currency" TO "service_role";



GRANT ALL ON SEQUENCE "public"."currency_currency_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."currency_currency_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."grade" TO "authenticated";
GRANT ALL ON TABLE "public"."grade" TO "service_role";



GRANT ALL ON SEQUENCE "public"."grade_grade_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."grade_grade_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."grade_size" TO "authenticated";
GRANT ALL ON TABLE "public"."grade_size" TO "service_role";



GRANT ALL ON TABLE "public"."login_attempt" TO "service_role";



GRANT ALL ON TABLE "public"."price_list" TO "authenticated";
GRANT ALL ON TABLE "public"."price_list" TO "service_role";



GRANT ALL ON SEQUENCE "public"."price_list_price_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."price_list_price_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."profiles" TO "authenticated";
GRANT ALL ON TABLE "public"."profiles" TO "service_role";



GRANT ALL ON TABLE "public"."receipt" TO "authenticated";
GRANT ALL ON TABLE "public"."receipt" TO "service_role";



GRANT ALL ON SEQUENCE "public"."receipt_receipt_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."receipt_receipt_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."rejection_disposition" TO "authenticated";
GRANT ALL ON TABLE "public"."rejection_disposition" TO "service_role";



GRANT ALL ON SEQUENCE "public"."rejection_disposition_disposition_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."rejection_disposition_disposition_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."rejection_disposition_disposition_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."rough_intake" TO "authenticated";
GRANT ALL ON TABLE "public"."rough_intake" TO "service_role";



GRANT ALL ON SEQUENCE "public"."rough_intake_intake_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."rough_intake_intake_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."sales_invoice" TO "authenticated";
GRANT ALL ON TABLE "public"."sales_invoice" TO "service_role";



GRANT ALL ON SEQUENCE "public"."sales_invoice_invoice_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."sales_invoice_invoice_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."sales_line" TO "authenticated";
GRANT ALL ON TABLE "public"."sales_line" TO "service_role";



GRANT ALL ON SEQUENCE "public"."sales_line_line_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."sales_line_line_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."size_bucket" TO "authenticated";
GRANT ALL ON TABLE "public"."size_bucket" TO "service_role";



GRANT ALL ON SEQUENCE "public"."size_bucket_size_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."size_bucket_size_id_seq" TO "service_role";



GRANT SELECT,INSERT,REFERENCES,TRIGGER,TRUNCATE,MAINTAIN ON TABLE "public"."stock_movement" TO "authenticated";
GRANT ALL ON TABLE "public"."stock_movement" TO "service_role";



GRANT ALL ON SEQUENCE "public"."stock_movement_movement_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."stock_movement_movement_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."v_sales_line" TO "authenticated";
GRANT ALL ON TABLE "public"."v_sales_line" TO "service_role";



GRANT ALL ON TABLE "public"."v_invoice" TO "authenticated";
GRANT ALL ON TABLE "public"."v_invoice" TO "service_role";



GRANT ALL ON TABLE "public"."v_receivables_ageing" TO "authenticated";
GRANT ALL ON TABLE "public"."v_receivables_ageing" TO "service_role";



GRANT ALL ON TABLE "public"."v_reconciliation" TO "authenticated";
GRANT ALL ON TABLE "public"."v_reconciliation" TO "service_role";



GRANT ALL ON TABLE "public"."v_stock_import_batch" TO "anon";
GRANT ALL ON TABLE "public"."v_stock_import_batch" TO "authenticated";
GRANT ALL ON TABLE "public"."v_stock_import_batch" TO "service_role";



GRANT ALL ON TABLE "public"."v_stock_movement" TO "authenticated";
GRANT ALL ON TABLE "public"."v_stock_movement" TO "service_role";



GRANT ALL ON TABLE "public"."v_stock_position" TO "authenticated";
GRANT ALL ON TABLE "public"."v_stock_position" TO "service_role";









ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES TO "postgres";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES TO "anon";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES TO "authenticated";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES TO "service_role";






ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS TO "postgres";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS TO "anon";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS TO "authenticated";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS TO "service_role";






ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES TO "postgres";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES TO "anon";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES TO "authenticated";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES TO "service_role";































