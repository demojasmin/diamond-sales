-- ---------------------------------------------------------------------------
-- 0029 · Three grades the client's printed sheet uses and the catalogue does
--        not have: FL, 1MB and -2 MB.
--
-- WHY
--
-- JADU 11-5-26.pdf holds 69.72 ct across these three, and the stock importer
-- refuses the whole sheet rather than place 69 of its 72 lines: an import
-- REPLACES, so a holding that cannot be placed is not skipped, it is gone.
-- Everything else on that sheet reconciles to its own printed totals, so the
-- catalogue is the only thing missing.
--
--     FL      38.23 ct
--     1MB     25.35 ct
--     -2 MB    6.14 ct
--
-- NAMED AS PRINTED
--
-- Codes are what the sheet prints, not what the catalogue's own NO 1 / NO 1 BB
-- convention would suggest, because nobody has said what these stand for and a
-- guessed display name would be wrong on every screen for as long as the grade
-- exists. Renaming a grade later is one update; unpicking a wrong name that has
-- been read as correct for a month is not.
--
-- The sheet prints "-2 MB" with a leading comma and "-2 BB" with a leading
-- period -- artefacts of how the cell is set, not part of the name. They go in
-- as ALIASES, which is what that column is for and how the workbook importer
-- has always absorbed a client's spellings.
--
-- NOT ADDED: "LB 3" and "-2 BB" also appear on that sheet and are also unknown,
-- but both hold 0.00 ct. A zero-weight line names no holding, so the importer
-- passes over it and neither blocks anything. They can be added when they
-- carry stock and somebody can say what they are.
-- ---------------------------------------------------------------------------

insert into public.grade (code, display_name, aliases, sort_order, active)
values
    ('FL',    'FL',    'F L;FL1',            24, true),
    ('1MB',   '1MB',   '1 MB;NO1MB;NO 1 MB', 25, true),
    ('-2 MB', '-2 MB', ',-2 MB;-2MB;,-2MB',  26, true)
on conflict (code) do nothing;


-- ---------------------------------------------------------------------------
-- The size pairings, or these grades import and can never be SOLD.
--
-- 0018 guards sales_line with a trigger against grade_size, and seeded it with
-- "every grade takes every size except -2, which is NO 1 and NO 1 BB only". A
-- grade added without its pairings passes the stock import -- that trigger is
-- on sales_line, not stock_movement -- and then fails at the first invoice
-- with "Grade FL does not use size 14+", which reads as a bug rather than as a
-- missing catalogue row.
--
-- Same rule as 0018, so these three behave exactly like their neighbours.
-- ---------------------------------------------------------------------------
insert into public.grade_size (grade_id, size_id)
select g.grade_id, s.size_id
  from public.grade g
  cross join public.size_bucket s
 where g.code in ('FL', '1MB', '-2 MB')
   and s.code <> '-2'
on conflict do nothing;
