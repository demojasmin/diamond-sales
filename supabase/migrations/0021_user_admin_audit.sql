-- ---------------------------------------------------------------------------
-- 0021 · Let the audit trail record actions against a user account.
--
-- audit_log.record_id is a bigint, which every table in this schema uses for its
-- key -- except profiles, whose id is the uuid from auth.users. So account
-- actions had nowhere to record WHICH account they touched: the row would land
-- with a null record_id and the id buried in new_values, where "everything ever
-- done to this login" stops being an indexed lookup.
--
-- One nullable column. Nothing existing changes: record_id keeps its meaning for
-- every other table, and the action check constraint is untouched --
-- admin-users writes INSERT or UPDATE and names the specific operation inside
-- new_values.admin_action, so a widened constraint is not needed and the
-- existing audit triggers carry on as they are.
-- ---------------------------------------------------------------------------
alter table public.audit_log
    add column if not exists record_uuid uuid;

comment on column public.audit_log.record_uuid is 'The key of the audited row when that key is a uuid rather than a bigint. public.profiles is the only such table today; record_id stays null for these rows.';

-- Partial: only account rows use it, and the trail is written far more often
-- than it is read.
create index if not exists audit_log_record_uuid_idx
    on public.audit_log (record_uuid, changed_at desc)
 where record_uuid is not null;

-- ---------------------------------------------------------------------------
-- No policy change. 0004 already has this right and adding to it would only
-- confuse the picture:
--
--   * audit_read already lets managers and owners SELECT, and RLS policies are
--     OR-ed -- a second owner-only policy would grant nothing new while reading
--     as though it restricted something.
--   * there is deliberately no INSERT policy, and insert/update/delete are
--     revoked from authenticated, so nobody holding an app login can forge a
--     row. admin-users writes through service_role, which carries BYPASSRLS and
--     so is unaffected by the FORCE ROW LEVEL SECURITY 0004 sets.
--
-- The trail stays append-only-by-privileged-caller, exactly as it was.
-- ---------------------------------------------------------------------------
