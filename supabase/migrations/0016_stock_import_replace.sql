-- ---------------------------------------------------------------------------
-- 0016 · Let the stock import replace its own previous rows.
--
-- The stock import is "replace, not merge": it clears what it imported before,
-- then inserts the workbook. That step could never run. `authenticated` has
-- INSERT on stock_movement but no DELETE -- the append-only ledger, enforced in
-- Postgres -- so the client fails with 42501.
--
-- The first import worked only because there was nothing to delete. A second
-- one fails outright. (It fails cleanly: the delete is the first statement, so
-- nothing is written and no stock is duplicated. But the feature is unusable
-- more than once.)
--
-- Granting DELETE on stock_movement would fix it and destroy the guarantee that
-- makes the ledger worth trusting: anyone holding an app login could then erase
-- history. Instead this is one SECURITY DEFINER function that runs with the
-- owner's rights and can delete NOTHING except rows it stamped itself --
-- ref_type = 'stock_import'. A hand-entered intake writes 'rough_intake', a
-- sale writes 'sales_line'; neither can be reached from here.
-- ---------------------------------------------------------------------------
create or replace function public.delete_imported_stock()
returns integer
language plpgsql
security definer
-- Pinned: a SECURITY DEFINER function without this can be redirected to another
-- schema's tables by whoever calls it.
set search_path = public
as $$
declare
    v_ids bigint[];
    v_deleted integer;
begin
    -- The parcels behind the movements, captured before the movements go.
    select coalesce(array_agg(distinct ref_id), '{}')
      into v_ids
      from public.stock_movement
     where ref_type = 'stock_import'
       and ref_id is not null;

    delete from public.stock_movement
     where ref_type = 'stock_import';
    get diagnostics v_deleted = row_count;

    -- Children first would be wrong here: the movement points AT the intake, so
    -- the movement goes first and the parcel follows.
    if array_length(v_ids, 1) is not null then
        delete from public.rough_intake
         where intake_id = any(v_ids);
    end if;

    return v_deleted;
end;
$$;

comment on function public.delete_imported_stock() is
    'Clears the previous stock import so the next one can replace it. Deletes only '
    'stock_movement rows stamped ref_type = ''stock_import'' and the rough_intake '
    'parcels they reference. Cannot touch hand-entered intakes or sales movements.';

revoke all on function public.delete_imported_stock() from public;
grant execute on function public.delete_imported_stock() to authenticated;
