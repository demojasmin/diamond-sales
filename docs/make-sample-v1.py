"""Re-lays the client's `sample.pdf` figures into the Stock Sheet v1 format.

    python docs/make-sample-v1.py

Not one figure is altered. What changes is the shape: v1's heading row, its carat/rate pairs, a
subtotal row whose grade cell is empty, and a TOTAL row -- in place of the two-row header and the
detached Cts/Price/Amount box that made the original unreadable.

Writes a real PDF the way DiamondCalc.Tests/MiniPdf.cs does: Helvetica text at coordinates with a
hand-built xref, which is what the importer reads. No dependency beyond the standard library.
"""

import os

# ── the sheet, exactly as sample.pdf prints it ──────────────────────────────────────
# Columns are in v1's own order. The original runs 1/4 first; only the ORDER moves, and every
# figure stays with the sieve it was printed under.
SIZES = ["6.5-", "6.5+", "11+", "1/6", "1/5", "1/4"]

# grade -> {size: (carats, rate)}. A size absent here is one the original left blank.
SHEET = {
    "1MB":   {"6.5-": (8.96, 58000), "6.5+": (9.85, 63000), "11+": (7.71, 68000),
              "1/6": (2.58, 58000), "1/5": (0.62, 63000)},
    "#":     {"6.5-": (4.51, 47000), "6.5+": (16.15, 51000), "11+": (29.51, 53000),
              "1/6": (12.07, 47000), "1/5": (5.17, 51000), "1/4": (0.71, 53000)},
    "Ex1":   {"6.5-": (1.77, 40000), "6.5+": (9.18, 44000), "11+": (16.57, 47000),
              "1/6": (7.81, 40000), "1/5": (4.29, 44000), "1/4": (0.71, 47000)},
    "2":     {"6.5-": (1.43, 37500), "6.5+": (6.48, 40500), "11+": (13.61, 43000),
              "1/6": (7.17, 37500), "1/5": (3.37, 40500), "1/4": (0.49, 43000)},
    "Dx":    {"6.5-": (1.07, 35000), "6.5+": (5.5, 38000), "11+": (11.38, 40000),
              "1/6": (5.97, 35000), "1/5": (3.2, 38000), "1/4": (0.47, 40000)},
    "3":     {"6.5-": (0.79, 32500), "6.5+": (4.63, 35500), "11+": (11.92, 37500),
              "1/6": (6.47, 32500), "1/5": (2.91, 35500), "1/4": (0.24, 37500)},
    "4":     {"6.5-": (0.77, 30500), "6.5+": (3.27, 32000), "11+": (10.48, 34000),
              "1/6": (5.32, 30500), "1/5": (2.13, 32000), "1/4": (0.5, 34000)},
    "5":     {"6.5-": (0.39, 25500), "6.5+": (2.58, 28000), "11+": (6.89, 30000),
              "1/6": (3.57, 25500), "1/5": (2.41, 28000)},
    "6":     {"6.5-": (0.28, 22500), "6.5+": (1.6, 25000), "11+": (4.49, 27000),
              "1/6": (2.23, 22500), "1/5": (0.62, 25000)},
    "7":     {"6.5-": (0.11, 19000), "6.5+": (0.9, 21000), "11+": (1.04, 23000),
              "1/6": (0.45, 19000)},
    "GH VS": {"11+": (1.87, 22000), "1/6": (1.07, 22000), "1/5": (0.56, 25000)},
}
ORDER = ["1MB", "#", "Ex1", "2", "Dx", "3", "4", "5", "6", "7", "GH VS"]

# The subtotal line the original prints, kept to the paisa rather than rounded: R12's whole-rupee
# rule is about the rates a person types on a grade row, and rounding here would be changing a
# figure the sheet states.
SUBTOTAL = {
    "6.5-": (20.08, "47859.56"), "6.5+": (60.14, "45222.90"), "11+": (115.47, "44200.57"),
    "1/6": (54.71, "37524.95"), "1/5": (25.28, "40269.78"), "1/4": (3.12, "43868.59"),
}
# The original's summary box, moved onto a TOTAL row where v1 expects it. The box's third figure,
# Amount 11992445, is 278.8 x 43014.51 and is not carried: v1's TOTAL row is label, carats, rate.
TOTAL_CT, TOTAL_RATE = "278.80", "43014.51"
DATE = "09-08-2026"
TITLE = "STOCK SHEET"

# ── geometry ────────────────────────────────────────────────────────────────────────
PAGE_W, PAGE_H = 842, 595            # A4 landscape, per R2
LEFT = 62
GRADE_W = 70
CT_W, RATE_W = 46, 62
PAIR_W = CT_W + RATE_W
ROW_H = 20
HEAD_Y = 455                          # baseline of the heading row

def ct_centre(i):
    return LEFT + GRADE_W + i * PAIR_W + CT_W / 2

def rate_centre(i):
    return LEFT + GRADE_W + i * PAIR_W + CT_W + RATE_W / 2

RIGHT = LEFT + GRADE_W + len(SIZES) * PAIR_W

# ── PDF plumbing ────────────────────────────────────────────────────────────────────
FONT, BOLD = 10, 10

def width(text, size):
    em = 0
    for c in text:
        em += (0.556 if c.isdigit()
               else 0.278 if c in ".,: "
               else 0.584 if c == "+"
               else 0.333 if c == "-"
               else 0.667)
    return em * size

def esc(text):
    return text.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")

parts = []

def text(s, x, y, *, centre=True, bold=False, size=None):
    size = size or (BOLD if bold else FONT)
    at = x - width(s, size) / 2 if centre else x
    parts.append(f"BT /{'F2' if bold else 'F1'} {size} Tf 1 0 0 1 {at:.2f} {y:.2f} Tm ({esc(s)}) Tj ET")

def line(x1, y1, x2, y2, w=0.6):
    parts.append(f"{w} w {x1:.2f} {y1:.2f} m {x2:.2f} {y2:.2f} l S")

def rect_fill(x, y, w, h, rgb):
    parts.append(f"{rgb[0]:.3f} {rgb[1]:.3f} {rgb[2]:.3f} rg {x:.2f} {y:.2f} {w:.2f} {h:.2f} re f 0 0 0 rg")

# ── the two lines above the table · R7 ──────────────────────────────────────────────
text(TITLE, PAGE_W / 2, 520, bold=True, size=13)
text(f"DATE: {DATE}", PAGE_W / 2, 500, size=11)

rows = len(ORDER)
top = HEAD_Y + 13                      # top edge of the heading row

# the amber heading band, so the sheet reads like the template
rect_fill(LEFT, HEAD_Y - 7, RIGHT - LEFT, 20, (0.906, 0.639, 0.235))

# ── the heading row · R8 ────────────────────────────────────────────────────────────
text("GRADE", LEFT + GRADE_W / 2, HEAD_Y, bold=True)
for i, size in enumerate(SIZES):
    text(size, ct_centre(i), HEAD_Y, bold=True)
    text("RATE", rate_centre(i), HEAD_Y, bold=True, size=8)

# ── one row per grade · R9, R11, R12 ────────────────────────────────────────────────
# A cell the original left blank prints 0.00 and 0 here, which is R11's rule and says the same
# thing: the office counted that bucket and found nothing in it.
for r, grade in enumerate(ORDER):
    y = HEAD_Y - (r + 1) * ROW_H
    text(grade, LEFT + 5, y, centre=False)
    for i, size in enumerate(SIZES):
        ct, rate = SHEET[grade].get(size, (0.0, 0))
        text(f"{ct:.2f}", ct_centre(i), y)
        text(f"{rate:g}", rate_centre(i), y)

# ── the subtotal row · R13 · its grade cell stays empty ─────────────────────────────
sub_y = HEAD_Y - (rows + 1) * ROW_H
for i, size in enumerate(SIZES):
    ct, rate = SUBTOTAL[size]
    text(f"{ct:.2f}", ct_centre(i), sub_y, bold=True)
    text(rate, rate_centre(i), sub_y, bold=True)

# ── the TOTAL row · R14 ─────────────────────────────────────────────────────────────
total_y = sub_y - ROW_H - 14
text("TOTAL", LEFT + 5, total_y, centre=False, bold=True)
text(TOTAL_CT, ct_centre(0), total_y, bold=True)
text(TOTAL_RATE, rate_centre(0), total_y, bold=True)

# ── the rules ───────────────────────────────────────────────────────────────────────
for r in range(rows + 2):                                  # heading + grades + subtotal
    y = top - r * ROW_H
    line(LEFT, y, RIGHT, y, 1.4 if r in (0, 1, rows + 1) else 0.6)
line(LEFT, top - (rows + 2) * ROW_H, RIGHT, top - (rows + 2) * ROW_H, 1.4)

for x in [LEFT, LEFT + GRADE_W] + [
        LEFT + GRADE_W + i * PAIR_W + w
        for i in range(len(SIZES)) for w in (CT_W, PAIR_W)]:
    line(x, top, x, top - (rows + 2) * ROW_H, 1.4 if x in (LEFT, RIGHT, LEFT + GRADE_W) else 0.6)

# ── write it ────────────────────────────────────────────────────────────────────────
stream = "\n".join(parts) + "\n"
objects = [
    "<</Type/Catalog/Pages 2 0 R>>",
    "<</Type/Pages/Kids[3 0 R]/Count 1>>",
    f"<</Type/Page/Parent 2 0 R/MediaBox[0 0 {PAGE_W} {PAGE_H}]"
    "/Resources<</Font<</F1 5 0 R/F2 6 0 R>>>>/Contents 4 0 R>>",
    f"<</Length {len(stream)}>>\nstream\n{stream}endstream",
    "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
    "<</Type/Font/Subtype/Type1/BaseFont/Helvetica-Bold>>",
]

pdf, offsets = "%PDF-1.4\n", []
for i, obj in enumerate(objects):
    offsets.append(len(pdf))
    pdf += f"{i + 1} 0 obj\n{obj}\nendobj\n"

xref = len(pdf)
pdf += f"xref\n0 {len(objects) + 1}\n0000000000 65535 f \n"
pdf += "".join(f"{o:010d} 00000 n \n" for o in offsets)
pdf += f"trailer\n<</Size {len(objects) + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF"

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sample-v1.pdf")
with open(out, "w", encoding="ascii", newline="") as f:
    f.write(pdf)

# ── the sheet must add up before it is worth sending ────────────────────────────────
for size in SIZES:
    read = sum(SHEET[g].get(size, (0, 0))[0] for g in ORDER)
    assert abs(read - SUBTOTAL[size][0]) < 0.005, f"{size}: rows {read}, subtotal {SUBTOTAL[size][0]}"

grand = sum(SUBTOTAL[s][0] for s in SIZES)
assert abs(grand - float(TOTAL_CT)) < 0.005, f"TOTAL {TOTAL_CT}, subtotals {grand}"

print("wrote", out)
print(f"{len(ORDER)} grades x {len(SIZES)} sizes")
print(f"every subtotal matches its column, and they add to {grand:.2f} ct")
