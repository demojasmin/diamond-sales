-- ---------------------------------------------------------------------------
-- Undo 0061: the checked wrappers go, each <name>__unchecked takes its original name back, and
-- EXECUTE is restored exactly as it was before (authenticated on all of them; anon as well on
-- clear_login_failures). profiles.active defaults to true again. One transaction.
-- ---------------------------------------------------------------------------
begin;

do $$
declare
    f text;
begin
    foreach f in array array[
        'import_stock(date, jsonb, boolean, uuid, text)',
        'replace_imported_sales(jsonb)',
        'add_grade(text, text)',
        'add_size(text, text)',
        'edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text)',
        'reserve_line(uuid, text, bigint, bigint, numeric)',
        'release_line(uuid, text)',
        'release_entry(uuid)',
        'clear_login_failures(text)']
    loop
        if to_regprocedure('public.' || split_part(f, '(', 1) || '__unchecked(' || split_part(f, '(', 2)) is not null then
            execute format('drop function if exists public.%s', f);
            execute format('alter function public.%s rename to %I',
                           split_part(f, '(', 1) || '__unchecked(' || split_part(f, '(', 2), split_part(f, '(', 1));
        end if;
        execute format('grant execute on function public.%s to authenticated, service_role', f);
    end loop;
end;
$$;

grant execute on function public.clear_login_failures(text) to anon;
grant execute on function public.delete_imported_stock() to authenticated;
grant execute on function public.clear_import_edits(bigint[]) to authenticated;
grant execute on function public.imported_edit_delta(bigint, jsonb) to authenticated;

drop function if exists public.require_role(text[]);
alter table public.profiles alter column active set default true;

commit;

notify pgrst, 'reload schema';
