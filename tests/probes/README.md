# Probe suites

Offscreen tests that drive the **real** `MainWindow` and the **live** Supabase database. Each
`Program.<suite>.bak` is one suite; `run_all.sh` copies one over `Program.cs`, builds, runs it, and
moves on. Only one compiles at a time because each is a top-level program.

```bash
bash tests/probes/run_all.sh          # all suites
```

To run one:

```bash
cp Program.margin.bak Program.cs && dotnet run -c Release
```

## What you need

**A signed-in desktop.** The suites reuse the app's saved session. Open Solitaire Desk and sign in
once; nothing here stores or asks for a password. The session file is encrypted with DPAPI and can
only be read by the Windows account that created it, so it cannot be copied to another machine.

**The workbooks and the printed sheet.** In whatever folder `PROBE_DATA_DIR` points at:

| file | used by |
|---|---|
| `stk BKC-JAN.xlsx` | `realfile`, `stockvalidate`, `reimport` — the real stock workbook |
| `stk BKC-JAN-dummy.xlsx` | `stockvalidate` — the corrupted one, which must be **refused** |
| `Sale File Sample.xlsx` | `salesreplace` — the sales workbook |
| `JADU APR-AUG'26.pdf` | `pdfparse`, `pdfimport` — the client's printed stock sheet |
| `stk 13-08-26.xlsx` | `pdfimport`, `excelfallback` — **the workbook the ledger currently holds** |

They are **not in the repository**: they hold real customer sales data. Get them from whoever runs
the migration. Without them those suites fail; the rest do not care.

> `stk 13-08-26.xlsx` is not interchangeable with the other stock workbooks. `pdfimport` and
> `excelfallback` restore the live position by re-importing it, and Downloads holds nine stock
> workbooks that are all different counts — restoring from the wrong one silently swaps the live
> position for an older one. Both suites refuse to start unless the file's carats and row count
> match the ledger exactly. When the live import is replaced, this filename changes with it.

## Configuration

Both are optional and both default sensibly.

| variable | default | what it is |
|---|---|---|
| `PROBE_DATA_DIR` | `%USERPROFILE%\Downloads` | where the workbooks live |
| `SOLITAIRE_SESSION` | `%LOCALAPPDATA%\SolitaireDesk\session.dat` | the app's saved session |

```bash
PROBE_DATA_DIR="D:/diamond-data" bash tests/probes/run_all.sh
```

Everything machine-specific is resolved through `ProbeEnv.cs`. If you find an absolute path in a
suite, it is a bug — they all used to carry one developer's home directory and it made them
unrunnable anywhere else.

## Reading the output

`run_all.sh` prints each suite's **last line**, which for most is a verdict. Four
(`brief`, `dispo`, `schema`, `realfile`) end with data instead, so a tail is not a pass — run those
directly and check the exit code, or look for `FAIL` lines.

## Most of these write to the live database

Not a warning to skip. `oversell` flips `app_config.negative_stock`, `idem` and `convcost` post
movements and reverse them, `salesreplace` **replaces the entire imported sales book**, and
`reimport` replaces the stock import. They clean up after themselves and the ledger is append-only,
so corrections appear as compensating rows rather than deletions — but do not run these against a
database you are not willing to change.

`margin` takes a `write` argument to opt into its immutability check; without it, it is read-only.

`pdfimport` replaces the imported stock position three times and puts it back in a `finally` block.
`excelfallback` re-imports the position that is already there. Both refuse to start unless the
restore file provably matches the ledger, so a run that could not put the position back does not
begin. `pdfparse` is read-only and needs no database write at all.

Never add a call to a mutating RPC as an "existence check". Calling `delete_imported_stock()` to
see whether it existed wiped the live stock import twice in one afternoon. `schema` carries a
comment saying so.
