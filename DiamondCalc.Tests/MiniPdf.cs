using System.Globalization;
using System.IO;
using System.Text;

namespace DiamondCalc.Tests;

/// <summary>
/// Writes the smallest PDF PdfPig will read: one page, one standard font, text drawn at the
/// coordinates given. Same reasoning as <see cref="MiniXlsx"/> — the import checks run against a
/// real file through the real reader, not against a stubbed one.
///
/// It exists because the printed sheets the reader was built against are real customer stock and
/// are not in the repository, so the one row that matters most here — a holding the sheet printed
/// no grade name against — could not be tested at all. Every sheet we hold prints 0.00 on its
/// unnamed rows.
///
/// Text is CENTRED on the x given, because that is how the sheets set their figures and how
/// <see cref="DiamondDesktop.PdfStockFile"/> finds its columns. Helvetica's advance widths are
/// approximated closely enough for that: the reader's column tolerance is 6pt and the worst error
/// here is well under one.
/// </summary>
public static class MiniPdf
{
    private const double FontSize = 10;

    /// <summary>
    /// One stock sheet carrying what no real sheet we hold carries: two rows the paper prints no
    /// grade name against, both holding actual carats.
    ///
    /// Shared by the checks in Program.cs and by `sample-pdf`, which writes it out so the same
    /// sheet can be imported through the real app. A fixture the tests and the desk disagree about
    /// is worth less than either.
    ///
    ///     1 BB     4.00 @ 30000    2.00 @ 40000
    ///     (blank)  1.50 @ 25000    0.50 @ 35000
    ///     LB 2     1.00 @ 10000    1.00 @ 20000
    ///     (blank)  0.25 @ 12000    0.75 @ 22000
    ///                 6.75            4.25       = 11.00 ct
    /// </summary>
    public static readonly List<(string Text, double CentreX, double Y)> UnnamedRowSheet =
    [
        // The two lines Stock Sheet v1 puts above the table, and its heading row. All three sat
        // missing here until somebody held the generated sheet against the spec: the reader never
        // needed them -- it starts at the heading row and ignores everything above it -- so a
        // fixture that was NOT a v1 sheet passed every check. A test file the client could not
        // have produced tests less than it looks like it does.
        ("STOCK SHEET", 200, 580),
        ("DATE: 01-08-2026", 200, 560),
        ("GRADE", 75, 530),
        ("6.5-", 160, 530), ("RATE", 230, 530), ("6.5+", 300, 530), ("RATE", 370, 530),
        ("1 BB", 75, 510), ("4.00", 160, 510), ("30000", 230, 510), ("2.00", 300, 510), ("40000", 370, 510),
        ("1.50", 160, 490), ("25000", 230, 490), ("0.50", 300, 490), ("35000", 370, 490),
        ("LB 2", 75, 470), ("1.00", 160, 470), ("10000", 230, 470), ("1.00", 300, 470), ("20000", 370, 470),
        ("0.25", 160, 450), ("12000", 230, 450), ("0.75", 300, 450), ("22000", 370, 450),
        ("6.75", 160, 420), ("25259", 230, 420), ("4.25", 300, 420), ("31529", 370, 420),
        ("TOTAL", 75, 390), ("11.00", 160, 390), ("27682", 230, 390),
    ];


    /// <param name="cells">Each cell's text, the x its centre sits on, and its baseline y.</param>
    public static void Write(string path, IEnumerable<(string Text, double CentreX, double Y)> cells)
    {
        var content = new StringBuilder();
        foreach (var (text, centreX, y) in cells)
            content.Append(CultureInfo.InvariantCulture,
                $"BT /F1 {FontSize} Tf 1 0 0 1 {centreX - Width(text) / 2:0.###} {y:0.###} Tm ({Escape(text)}) Tj ET\n");

        string[] objects =
        [
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 842 595]"
                + "/Resources<</Font<</F1 5 0 R>>>>/Contents 4 0 R>>",
            $"<</Length {content.Length}>>\nstream\n{content}endstream",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        ];

        // Everything written here is ASCII, so a character count IS the byte offset the xref needs.
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture,
            $"trailer\n<</Size {objects.Length + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF");

        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
    }

    /// Helvetica's advance widths, to the four groups that matter on a stock sheet.
    private static double Width(string text)
    {
        double em = 0;
        foreach (char c in text)
            em += c switch
            {
                >= '0' and <= '9' => 0.556,
                '.' or ',' or ' ' => 0.278,
                '+' => 0.584,
                '-' => 0.333,
                _ => 0.667,
            };
        return em * FontSize;
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
