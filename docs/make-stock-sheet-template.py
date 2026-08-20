"""Writes the Stock Sheet v1 Excel template the client fills in and saves as PDF.

    python docs/make-stock-sheet-template.py

Generated rather than committed as a binary, so the template and the specification cannot drift:
every rule the importer enforces is applied here in one place, with the rule named beside it.

Needs openpyxl (pip install openpyxl). Nothing in the app depends on this script -- it produces a
file for the office, and it runs when the format changes, not on every build.
"""

# openpyxl is a run-time requirement of this script and of nothing else in the repository, so the
# editor's interpreter may well not have it. That is a setup fact about one machine, not a defect
# in the file, and it is the whole of reportMissingModuleSource. Select an interpreter that has
# openpyxl and the warning is moot either way.
# pyright: reportMissingModuleSource=false

from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter
from openpyxl.worksheet.datavalidation import DataValidation
import os

# The six sieves the client's sheet carries, in the order it prints them. Change this list and the
# whole sheet follows -- headings, formulas, widths and the TOTAL row.
SIZES = ["6.5-", "6.5+", "11+", "1/6", "1/5", "1/4"]

# The grades the sheet prints, in the order it prints them. Spelled exactly as the importer
# resolves them: these are the marks, not the catalogue's own codes.
GRADES = [
    "1 MB", "1 BB", "FL", "#", "EXTRA", "2", "DX1", "3", "4", "5", "6", "7",
    "TOP co", "color", "OW", "LC 1", "LC 2", "GH", "LB 1", "LB 2",
]

# Named because each is used across the sheet and the notes, and a format typed out four times is
# four chances to type it differently (S1192). CARATS and RATE are the two number formats R11 and
# R12 insist on: two decimals for a weight, whole rupees for a price.
TITLE = "STOCK SHEET"
TOTAL_LABEL = "TOTAL"
CARATS = "0.00"
RATE = "#,##0"
MIDDLE = "center"

AMBER = PatternFill("solid", fgColor="E7A33C")
THIN = Side(style="thin", color="000000")
MEDIUM = Side(style="medium", color="000000")
BOX = Border(left=THIN, right=THIN, top=THIN, bottom=THIN)
CENTRE = Alignment(horizontal=MIDDLE, vertical=MIDDLE)

wb = Workbook()
ws = wb.active
ws.title = TITLE

# ── the two lines above the table · R7 ──────────────────────────────────────────────
# The importer never reads these -- it starts at the heading row -- but the person checking the
# sheet against the screen does, and the operator keys the date in at import.
last_col = 1 + len(SIZES) * 2
ws.cell(1, 1, TITLE).font = Font(bold=True, size=13)
ws.merge_cells(start_row=1, start_column=1, end_row=1, end_column=last_col)
ws.cell(1, 1).alignment = CENTRE

ws.cell(2, 1, "DATE: DD-MM-YYYY").font = Font(size=11)
ws.merge_cells(start_row=2, start_column=1, end_row=2, end_column=last_col)
ws.cell(2, 1).alignment = CENTRE

# ── the heading row · R8 ────────────────────────────────────────────────────────────
# GRADE, then each size over its carat column with RATE beside it. No other number may appear on
# this row: the reader tells a heading from a row of holdings on exactly that.
HEAD = 3
ws.cell(HEAD, 1, "GRADE")
for i, size in enumerate(SIZES):
    ws.cell(HEAD, 2 + i * 2, size)
    ws.cell(HEAD, 3 + i * 2, "RATE")

for c in range(1, last_col + 1):
    cell = ws.cell(HEAD, c)
    cell.fill = AMBER
    cell.font = Font(bold=True)
    cell.alignment = CENTRE
    cell.border = BOX

# ── one row per grade · R9, R11, R12 ────────────────────────────────────────────────
# Carats pre-filled with 0.00 rather than left blank, because R11 says every carat cell carries a
# figure and an empty cell is the easiest rule in the whole spec to forget.
FIRST = HEAD + 1
LAST = FIRST + len(GRADES) - 1

for r, grade in enumerate(GRADES, start=FIRST):
    name = ws.cell(r, 1, grade)
    name.alignment = Alignment(horizontal="left", vertical=MIDDLE)
    name.border = BOX

    for i in range(len(SIZES)):
        ct = ws.cell(r, 2 + i * 2, 0)
        ct.number_format = CARATS
        ct.alignment = CENTRE
        ct.border = BOX

        rate = ws.cell(r, 3 + i * 2, 0)
        rate.number_format = RATE
        rate.alignment = CENTRE
        rate.border = BOX

# ── the subtotal row · R13 ──────────────────────────────────────────────────────────
# Its grade cell is EMPTY, and that emptiness is how the reader tells a subtotal from a holding.
# Formulas, so the arithmetic R15 insists on cannot be got wrong by hand.
SUB = LAST + 1
ws.cell(SUB, 1, "").border = Border(top=MEDIUM)

for i in range(len(SIZES)):
    ct_col = get_column_letter(2 + i * 2)
    rate_col = get_column_letter(3 + i * 2)

    ct = ws.cell(SUB, 2 + i * 2, f"=SUM({ct_col}{FIRST}:{ct_col}{LAST})")
    ct.number_format = CARATS

    # Weighted by carats where there are any, and the plain average of the column where there are
    # none -- which is what the sheet itself does, and what the app now prints back.
    rate = ws.cell(SUB, 3 + i * 2, (
        f"=IF(SUM({ct_col}{FIRST}:{ct_col}{LAST})=0,"
        f"ROUND(AVERAGEIF({rate_col}{FIRST}:{rate_col}{LAST},\">0\"),0),"
        f"ROUND(SUMPRODUCT({ct_col}{FIRST}:{ct_col}{LAST},{rate_col}{FIRST}:{rate_col}{LAST})"
        f"/SUM({ct_col}{FIRST}:{ct_col}{LAST}),0))"
    ))
    rate.number_format = RATE

    for cell in (ct, rate):
        cell.font = Font(bold=True)
        cell.alignment = CENTRE
        cell.border = Border(left=THIN, right=THIN, top=MEDIUM, bottom=THIN)

# ── the TOTAL row · R14 ─────────────────────────────────────────────────────────────
# TOTAL, total carats, average rate, in that order and nothing else on the line.
TOTAL = SUB + 2
ws.cell(TOTAL, 1, TOTAL_LABEL).font = Font(bold=True)
ws.cell(TOTAL, 1).alignment = Alignment(horizontal="left", vertical=MIDDLE)

ct_cells = ",".join(f"{get_column_letter(2 + i * 2)}{SUB}" for i in range(len(SIZES)))
total_ct = ws.cell(TOTAL, 2, f"=SUM({ct_cells})")
total_ct.number_format = CARATS

value = "+".join(
    f"{get_column_letter(2 + i * 2)}{SUB}*{get_column_letter(3 + i * 2)}{SUB}"
    for i in range(len(SIZES))
)
total_rate = ws.cell(TOTAL, 3, f"=IF(B{TOTAL}=0,0,ROUND(({value})/B{TOTAL},0))")
total_rate.number_format = RATE

for cell in (total_ct, total_rate):
    cell.font = Font(bold=True)
    cell.alignment = CENTRE

# ── shape · R2, R3, R4 ──────────────────────────────────────────────────────────────
ws.column_dimensions["A"].width = 14          # R4: the grade column, clear of the figures
for i in range(len(SIZES)):
    ws.column_dimensions[get_column_letter(2 + i * 2)].width = 9
    ws.column_dimensions[get_column_letter(3 + i * 2)].width = 11

ws.page_setup.orientation = "landscape"       # R2
ws.page_setup.paperSize = ws.PAPERSIZE_A4
ws.page_setup.fitToWidth = 1                  # one page across, so no column is cut
ws.page_setup.fitToHeight = 1
ws.sheet_properties.pageSetUpPr.fitToPage = True
ws.print_options.horizontalCentered = True
ws.freeze_panes = "B4"                        # the headings and the grade column stay put

# Whole rupees only: a rate with paise in it is the one number a person is most likely to type
# wrongly, and R12 refuses it.
rates = DataValidation(type="whole", operator="greaterThanOrEqual", formula1=0, allow_blank=False)
rates.error = "Rates are whole rupees per carat: 58000, not 58,000.50."
rates.errorTitle = "Whole rupees only"
ws.add_data_validation(rates)
for i in range(len(SIZES)):
    col = get_column_letter(3 + i * 2)
    rates.add(f"{col}{FIRST}:{col}{LAST}")

# ── a second sheet holding the rules, so the file explains itself ───────────────────
notes = wb.create_sheet("HOW TO USE")
for row in [
    ["Stock Sheet v1 - how to fill this in"],
    [],
    ["1", "Type carats in the size columns and rupees per carat in the RATE columns."],
    ["2", "Leave 0.00 where a bucket holds nothing. Never leave a carat cell blank."],
    ["3", "Do not add, remove or reorder columns. Do not insert a column between a size and"],
    ["", "its RATE, and do not put a serial number, a remark or a row total anywhere."],
    ["4", "Add grade rows if you need them, ABOVE the subtotal row, and put a name in"],
    ["", "column A on every one. A row with carats and no name imports under"],
    ["", "\"Unknown Grade\" and somebody has to sort it out afterwards."],
    ["5", "Put the date on row 2 as DD-MM-YYYY."],
    ["6", "The grey subtotal row and the TOTAL row calculate themselves. Do not type over"],
    ["", "them, and do not delete the empty cell in column A of the subtotal row."],
    [],
    ["Sending it", ""],
    ["7", "File > Save as > PDF. Never scan or photograph a printout: a scan holds a"],
    ["", "picture, not text, and no import can read it."],
    ["8", "Check it is one page. If the columns spill over, reduce the font - do not let it"],
    ["", "run onto a second page."],
    [],
    ["If a sheet is refused, the message names the figure the sheet prints and the figure the"],
    ["system read. That difference is almost always the fastest way to the answer."],
]:
    notes.append(row)

notes["A1"].font = Font(bold=True, size=13)
notes["A14"].font = Font(bold=True)
notes.column_dimensions["A"].width = 11
notes.column_dimensions["B"].width = 95

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "Stock-Sheet-v1-template.xlsx")
wb.save(out)


# ── read it back and hold it against the specification ──────────────────────────────
# The rules are in one file and the sheet is in another; this is what stops them drifting. It
# reads the SAVED file rather than the objects above, so a setting that did not survive the save
# is caught too.
from openpyxl import load_workbook

check = load_workbook(out)[TITLE]
head = [check.cell(HEAD, c).value for c in range(1, last_col + 1)]

assert head[0] == "GRADE", "R8: the heading row starts with GRADE"
assert head[1::2] == SIZES, f"R8: sizes across the heading row, got {head[1::2]}"
assert set(head[2::2]) == {"RATE"}, "R8: RATE over every second column"
assert not any(isinstance(h, (int, float)) for h in head),     "R8: the heading row carries no number but the size labels"

assert check.cell(SUB, 1).value in (None, ""),     "R13: the subtotal row's grade cell is empty, which is how it is recognised"
assert str(check.cell(SUB, 2).value).startswith("=SUM"), "R13: subtotals are formulas"

assert check.cell(TOTAL, 1).value == TOTAL_LABEL, "R14: the TOTAL row names itself"
assert str(check.cell(TOTAL, 2).value).startswith("=SUM"), "R14: total carats is a formula"
assert sum(1 for r in range(1, TOTAL + 1)
           if check.cell(r, 1).value == TOTAL_LABEL) == 1, "R14: exactly one TOTAL on the sheet"

for r in range(FIRST, LAST + 1):
    assert check.cell(r, 1).value, f"R9: row {r} carries a grade name"
    for i in range(len(SIZES)):
        assert check.cell(r, 2 + i * 2).number_format == CARATS, "R11: carats to two decimals"
        assert check.cell(r, 3 + i * 2).number_format == RATE, "R12: rates in whole rupees"
        assert check.cell(r, 2 + i * 2).alignment.horizontal == "center",             "R6: every number is centre-aligned"

assert check.page_setup.orientation == "landscape", "R2: A4 landscape"

print("wrote", out)
print(f"{len(GRADES)} grades x {len(SIZES)} sizes, subtotal row {SUB}, total row {TOTAL}")
print("checked against Stock Sheet v1: R2, R6, R8, R9, R11, R12, R13, R14 all hold")
