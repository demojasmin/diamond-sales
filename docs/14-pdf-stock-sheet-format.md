# The stock sheet PDF, as the importer reads it

The reference sheet is `kalpesh.pdf` — six sieve sizes, rates beside each, dated at the top,
subtotals and a TOTAL at the foot. Any sheet built the same way imports without a question
asked. This describes what the reader needs, what it will forgive, and what stops it.

Everything here was verified against five real sheets in four different layouts on 19 Aug 2026.

---

## The five things a sheet must have

**1 · Real text, not a picture.**
Export to PDF from the spreadsheet. A scan or a photograph of a printout holds no text, and
no amount of parsing recovers it. The importer says so plainly rather than guessing.

**2 · A heading row naming the sieve sizes.**
Two or more sizes across the top — `6.5-`, `6.5+`, `11+`, `1/6`, `1/5`, `1/4`. The row may
also carry words like `WEIGHT` and `RATE`. It must **not** carry any other number: a row of
carats is not a heading, and the reader tells them apart on exactly that.

**3 · The grade name at the left, clear of the first figure column.**
`1 BB`, `FL`, `#`, `TOP co`, `2`, `DX1` — the short forms are fine, and so are grades whose
names are digits. The name must sit to the **left** of the first weight column, not inside it.

**4 · One subtotal row per table.**
Directly under the data, carrying each size's total. It carries no grade name — that is how
the reader knows it is not a holding.

**5 · A TOTAL row.**
The word `TOTAL`, then the carat total, then the average rate. In that order, left to right.

---

## What it will forgive

| | |
|---|---|
| **Sizes written any way** | `6.5+` or `+6.5`, `2-` or `-2`, `1/5` or `0.2`, `1/4` or `0.25`. All resolve to one bucket |
| **`TOTAL` inside the first column** | Rather than out in the margin. Read relative to the word, not by position |
| **Two tables on one page** | Three sizes with their own totals, then three more below. Each is read separately |
| **Landscape or portrait** | `kalpesh.pdf` and `107.pdf` are the same stock in both, and read identically |
| **Rates, or no rates** | A sheet stating carats only imports fine; no price is invented |
| **`0.00` or a blank cell** | Both mean the bucket holds nothing. Neither creates a parcel |
| **Rows with no grade name** | Imported under the grade `Unknown Grade`, carats, rate and all |
| **Report labels for grades** | `#`, `DX1`, `TOP co`, `color` all resolve through the catalogue's aliases |
| **A rate column on an empty size** | `1/4` holding 0.00 throughout still states its rate, and it is read |

A blank grade cell used to refuse the whole file, on the reasoning that the row above is a guess
and so is the row below. True — but the consequence was a stock count nobody could import over one
empty cell, which loses every other row on the sheet as well. `Unknown Grade` is the third answer:
nothing guessed, nothing lost, and the carats sit on the Stock page under a name that says exactly
what is known about them. Move them to the real grade with an adjustment, the same way any other
mistaken bucket is corrected. Two such rows on one sheet stay two rows on the preview —
`Unknown Grade` and `Unknown Grade (2)` — and both import under the one grade.

The grade is seeded by `supabase/migrations/0039_unknown_grade.sql`. On a database that has not had
it applied, the sheet is refused with the usual offer to add the missing grade, and the name it
offers is `Unknown Grade`.

---

## What stops the import

Each of these refuses the **whole file**. Nothing partial is ever written, because a stock
import replaces the position — a sheet read 95% correctly would become the official figure
with nothing to say it was short.

**The totals do not reconcile.**
Every size subtotal is checked, and the grand total is checked. If what the reader adds up
disagrees with what the sheet prints, it stops and says both figures.

**A sieve size the catalogue does not have.**
Not fatal: it offers to add it, named exactly as the sheet prints it. Yes adds and re-reads,
no stops with nothing imported.

**A grade the catalogue does not have.**
Same offer, same wording, same result.

**A scan instead of text.** As above.

---

## The reference sheet, checked line by line

`kalpesh.pdf`, which every future sheet can be measured against:

```
22 printed rows      2 with no grade name, then 1 MB … LB 2
6 size columns       6.5-   6.5+   11+   1/6   1/5   1/4
67 holdings          every cell carrying weight

subtotals    21.35   64.36   18.53   2.53   0.99   0.00
rates        26,219  28,104  29,638  25,933 26,934 34,921
TOTAL        107.76 ct @ 27,933 /ct
```

`21.35 + 64.36 + 18.53 + 2.53 + 0.99 + 0.00 = 107.76` — and the importer refuses unless it
agrees with all seven of those figures.

Note `1/4`: zero carats the whole way down, and it still states a rate. That rate is the plain
average of the column, because there is no weight to weight it by. Every other column's rate is
value over weight. Both are read.

---

## Before sending a sheet

- Exported to PDF from the spreadsheet, not scanned
- Every grade row has its name in the left column
- The size heading row carries sizes and nothing else numeric
- Subtotals and TOTAL are present and correct on the sheet itself
- New sizes or grades are expected — the app will offer to add them

If a sheet is refused, the message names the figure the sheet states and the figure the reader
found. That difference is almost always the fastest way to the answer.

---

## For the developer

The rules above are enforced in `DiamondDesktop/PdfStockImport.cs` and asserted by
`tests/probes/suite-pdf-layouts.cs.txt` — 106 checks over five real sheets, each pinned to the
total printed on its own face rather than to whatever the parser currently produces.

Adding a layout to the table at the top of that suite picks up every invariant at once: reads
without refusing, totals exactly what it prints, finds every size column, never reads a rate as
a weight, lists no size twice, and rows summing to the plan total.

Those five sheets are real customer stock and are not in the repository, and every one of them
prints 0.00 on its unnamed rows — so the `Unknown Grade` case had no sheet to be tested against.
`DiamondCalc.Tests/MiniPdf.cs` writes one: a page of text at coordinates, read back through the
real reader. The checks are in `DiamondCalc.Tests/Program.cs` under `PDF-UNNAMED`, and they run
with everything else on `dotnet run --project DiamondCalc.Tests` — no database, no data folder.
