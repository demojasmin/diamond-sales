-- ===========================================================================
-- 0013 · Grade aliases for the spellings the sale workbook uses
--
-- docs/08 §4 resolves grade and size through the alias tables and treats an
-- unmapped code as an exception, never a guess. That is why importing the real
-- workbook skipped 750 of its 1,437 rows: the sheet writes hyphenated labels
-- ("NO-2", "LC-1", "LB-2") that no alias covered.
--
-- Eleven labels already resolved -- 1, 1 BB, 1BB, II, EX 1, COL, OW, GH,
-- TOP-COL, EXTRA, +14. These twelve are the remainder, each the same grade
-- written with a hyphen instead of a space, checked one by one against
-- grade.code rather than derived by a rule.
--
-- Sizes need no equivalent: "6.5+" is "+6.5" with the sign moved, which is
-- notation (MDM-004), and SaleFileImport.SizeAliasMap generates it from each
-- catalogue code. No column, no seed data.
--
-- Safe to run repeatedly. Additive only -- no code, name, id or existing alias
-- is changed.
-- ===========================================================================

begin;

update public.grade g
set    aliases = case
           when g.aliases is null or g.aliases = '' then t.alias
           else g.aliases || ';' || t.alias
       end
from (values
        ('NO 2',  'NO-2'), ('NO 3',  'NO-3'), ('NO 4',  'NO-4'), ('NO 5',  'NO-5'),
        ('NO 6',  'NO-6'), ('NO 7',  'NO-7'), ('NO DX', 'NO-DX'), ('LC 1',  'LC-1'),
        ('LC 2',  'LC-2'), ('LC 3',  'LC-3'), ('LB 1',  'LB-1'), ('LB 2',  'LB-2')
     ) as t(code, alias)
where  g.code = t.code
       -- The alias list is one ';'-separated column, so membership is tested
       -- against ';' || list || ';'. Without the sentinels, '%NO-2%' would also
       -- match a hypothetical "NO-25" and the alias would never be added.
  and  not (';' || coalesce(g.aliases, '') || ';') like ('%;' || t.alias || ';%');

commit;

-- ---------------------------------------------------------------------------
-- Verification · every row must read 'ok', and resolved must be 12
-- ---------------------------------------------------------------------------
select v.alias,
       v.code,
       g.aliases,
       case
           when g.grade_id is null then 'MISSING GRADE'
           when (';' || coalesce(g.aliases, '') || ';') like ('%;' || v.alias || ';%') then 'ok'
           else 'ALIAS NOT SET'
       end as status,
       count(*) filter (
           where (';' || coalesce(g.aliases, '') || ';') like ('%;' || v.alias || ';%')
       ) over () as resolved
from (values
        ('NO 2','NO-2'),('NO 3','NO-3'),('NO 4','NO-4'),('NO 5','NO-5'),
        ('NO 6','NO-6'),('NO 7','NO-7'),('NO DX','NO-DX'),('LC 1','LC-1'),
        ('LC 2','LC-2'),('LC 3','LC-3'),('LB 1','LB-1'),('LB 2','LB-2')
     ) as v(code, alias)
left join public.grade g on g.code = v.code
order by v.alias;
