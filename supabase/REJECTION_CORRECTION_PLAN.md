# Historical rejection correction: plan

**Status: proposal. Nothing here has been applied.** It needs the client's answers (step 1) and your approval before anything runs on Priya.

## Where things stand after migration 0055

- From 0055 on, **only sold carats leave stock**. A rejection is still recorded (on the invoice, on the Rejections screen, in the ledger) but no longer reduces the balance.
- Every rejection that **already** reduced stock (Priya: 28 rows, **439.90 ct**) is not added back. 0055 restates each one as an explicit adjustment: `ref_type = 'rejection_hold'`, one per invoice and bucket, with a reason that names the invoice. Every bucket reads exactly what it read before 0055; the migration checks this and rolls back if a single bucket would move.
- Correcting history means **removing holds**, and possibly reversing some SALE deductions. That is a decision, so it is a separate, explicit step.

## Answered (2026-10-09)

**The 3,905.65 ct imported on 03-09 is stock BEFORE the sales, not a count after them.** So deducting the imported sales from it was correct, and no sale was deducted twice. Options B and C below no longer apply; question 2 is closed. Question 3 is open for the 11-09 batch (292.85 ct) only.

With the rule now "rejected carats never reduce stock", that leaves **Option A**: release every rejection hold, **+439.90 ct**. It still needs your approval before it runs. Priya figures:

| | ct |
|---|---|
| Stock imported 03-09 (before sales) + 11-09 | 3,905.65 + 292.85 = 4,198.50 |
| − sold (imported 794.42 + app 25.05) | 819.47 |
| = **correct shelf stock under the new rule** | **3,379.03** |
| shelf today (rejections still deducted) | 2,939.13 |
| difference = the 439.90 ct of rejection holds | 439.90 |

LB 2 × +6.5 under Option A: 68.89 imported − 22.01 sold = **46.88 ct** (22.13 today + 24.75 rejected).

## What decided the correction

| # | Question for the client | Why it matters |
|---|---|---|
| 1 | When a buyer rejects part of a parcel, do those stones come back into **the same grade and sieve**? | If yes, every rejection deduction was wrong, and its hold should be released. If they go to another grade, they belong there instead (a conversion, not a release). If they leave the business, the holds are correct and stay. |
| 2 | What date does each stock PDF describe, and is it the stock **after** the sales made before that date? | The stock imported on 03-09 came from PDFs dated **09-08** (`107.pdf`, "DATE 09.8.26", 107.75 ct) and saved **13–18 August** (`Stk.pdf` + `JADU APR-AUG'26.pdf` = 3,133.16 ct, the first import batch). The imported sales file runs from **19-01-2026 to 03-09-2026**. If the PDFs were printed *after* those sales, every sale dated before the PDF was deducted a **second** time. |
| 3 | Which of the three other PDF batches imported on 03-09 (278.80, 385.94 ct) and the 11-09 batch (292.85 ct) correspond to which sheet and date? | Each bucket's cut-off date is the date of the sheet its stock came from. |

## The options (Priya figures, `PREVIEW_REJECTION_CORRECTION.sql`, sheet date 18-08-2026)

| Option | When it applies | Adds back |
|---|---|---|
| **A** | Q1 = same bucket, and the PDFs were **opening** stock (before the sales) | **439.90 ct**: every rejection hold released |
| **B** | Q1 = same bucket, and the PDFs were a count **after** the earlier sales | **882.46 ct**: every rejection, plus the sold carats of sales dated on or before the sheet |
| **C** | Q1 = stones do **not** come back, and the PDFs were a count after the earlier sales | **815.78 ct**: sold + rejected of sales dated on or before the sheet (those were already off the counted shelf) |
| **none** | Q1 = stones do not come back, and the PDFs were opening stock | 0: the holds stay; history was right |

Sales by date (posted, Priya):

| Invoice dated | Kind | Sold ct | Rejected ct |
|---|---|---|---|
| on/before 09-08 | imported | 19.68 | 0.37 |
| 10-08 to 18-08 | imported | 422.88 | 372.85 |
| 19-08 to 03-09 | imported | 351.86 | 41.62 |
| 19-08 to 03-09 | app (LB 2 +6.5, dated 28-08) | 22.01 | 24.75 |
| after 03-09 | app | 3.04 | 0.31 |

Most of the rejections (373.22 ct of 439.90) belong to sales dated **on or before 18 August**, the period the stock PDFs most likely already reflect.

## How the chosen option would be applied (later, after approval)

1. Back up (`tests/probes/tool-backup-tables.cs.txt -- Priya`).
2. Run `PREVIEW_REJECTION_CORRECTION.sql` with the confirmed sheet date; the client signs off the per-bucket figures.
3. One transaction, written as a reviewed migration:
   - **release holds:** delete the chosen `rejection_hold` rows (the audit trail keeps each deleted row);
   - **reverse double-counted sales** (options B and C only): one `ADJUST`, `ref_type = 'presheet_sale'`, per bucket, with a reason naming the sheet date. **Never** by deleting SALE rows, which would break the invoices and the reconciliation report.
4. Re-run `RECONCILE_STOCK_DISCREPANCY.sql` and compare with the preview.

Every step is reversible: the holds and adjustments are ordinary rows with reasons, and the backup holds the before-image.

## Not part of this correction

- The **46.60 ct reserved** by orphaned Sales entries. That is a separate fix (`release_entry` per entry, after the client confirms none of those sales is pending).
- **3,758 ct**: no state of the ledger ever totalled this (a full replay of every row written or deleted, 03-09 to today). The figure needs a screenshot of where it was seen.
