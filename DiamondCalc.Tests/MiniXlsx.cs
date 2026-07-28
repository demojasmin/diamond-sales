using System.IO;
using System.IO.Compression;
using System.Text;

namespace DiamondCalc.Tests;

/// <summary>
/// Writes the smallest workbook Excel and the importer will both accept, so the import checks run
/// against real .xlsx files instead of a stubbed reader. Text goes in as an inline string, which
/// keeps the package to five parts and needs no shared-string table.
///
/// The first row supplied lands on row 2, because that is where the sale workbook's headings live
/// and where <see cref="DiamondDesktop.SaleFileImport"/> looks for them.
/// </summary>
public static class MiniXlsx
{
    public static void Write(string path, string sheetName, IEnumerable<string[]> rows)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        Add(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);

        Add(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);

        Add(zip, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\""
            + " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
            + "<sheets><sheet name=\"" + Escape(sheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets>"
            + "</workbook>");

        Add(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);

        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        int number = 2;
        foreach (var row in rows)
        {
            sb.Append("<row r=\"").Append(number).Append("\">");
            for (int i = 0; i < row.Length; i++)
            {
                string value = row[i];
                if (string.IsNullOrEmpty(value)) continue;      // an absent cell, as Excel writes it
                string reference = Column(i) + number;
                if (decimal.TryParse(value, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out _))
                    sb.Append("<c r=\"").Append(reference).Append("\"><v>").Append(value)
                      .Append("</v></c>");
                else
                    sb.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t>")
                      .Append(Escape(value)).Append("</t></is></c>");
            }
            sb.Append("</row>");
            number++;
        }
        sb.Append("</sheetData></worksheet>");
        Add(zip, "xl/worksheets/sheet1.xml", sb.ToString());
    }

    private static string Column(int index) => ((char)('A' + index)).ToString();

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}
