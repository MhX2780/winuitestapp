using System.IO.Compression;
using System.Text;

namespace UGA;

/// <summary>
/// Converts a parsed markdown table (List of rows, each a List of cell strings,
/// first row = header) to shareable formats: Markdown text and a real .xlsx file.
/// The .xlsx is built by hand as a minimal OpenXML package (no third-party
/// dependency) — good enough for plain data tables with no formatting/formulas.
/// </summary>
public static class TableExportHelper
{
    /// <summary>Rebuilds a standard markdown pipe-table from parsed rows.</summary>
    public static string ToMarkdown(List<List<string>> rows)
    {
        if (rows.Count == 0) return "";
        var numCols = rows.Max(r => r.Count);
        var sb = new StringBuilder();

        string EscapeCell(string s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ").Trim();
        void WriteRow(List<string> row)
        {
            sb.Append('|');
            for (int c = 0; c < numCols; c++)
            {
                var cell = c < row.Count ? row[c] : "";
                sb.Append(' ').Append(EscapeCell(cell)).Append(" |");
            }
            sb.Append('\n');
        }

        WriteRow(rows[0]);
        sb.Append('|');
        for (int c = 0; c < numCols; c++) sb.Append(" --- |");
        sb.Append('\n');
        for (int r = 1; r < rows.Count; r++) WriteRow(rows[r]);

        return sb.ToString();
    }

    /// <summary>
    /// Writes a minimal .xlsx (single sheet, header row bolded, plain string
    /// cells only) to the given path using System.IO.Compression directly.
    /// </summary>
    public static void WriteXlsx(string path, List<List<string>> rows)
    {
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        void AddEntry(string entryName, string content)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        AddEntry("[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "</Types>");

        AddEntry("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");

        AddEntry("xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>");

        AddEntry("xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Table\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
            "</workbook>");

        // Style 0 = default, Style 1 = bold (used for header row)
        AddEntry("xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
            "<font><sz val=\"11\"/><name val=\"Calibri\"/><b/></font></fonts>" +
            "<fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>" +
            "<borders count=\"1\"><border/></borders>" +
            "<cellStyleXfs count=\"1\"><xf/></cellStyleXfs>" +
            "<cellXfs count=\"2\">" +
            "<xf fontId=\"0\" applyFont=\"1\"/>" +
            "<xf fontId=\"1\" applyFont=\"1\"/>" +
            "</cellXfs>" +
            "</styleSheet>");

        AddEntry("xl/worksheets/sheet1.xml", BuildSheetXml(rows));
    }

    private static string BuildSheetXml(List<List<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<sheetData>");

        for (int r = 0; r < rows.Count; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            var row = rows[r];
            for (int c = 0; c < row.Count; c++)
            {
                var cellRef = $"{ColumnLetter(c)}{r + 1}";
                var styleAttr = r == 0 ? " s=\"1\"" : "";
                var text = EscapeXml(row[c] ?? "");
                sb.Append($"<c r=\"{cellRef}\"{styleAttr} t=\"inlineStr\"><is><t xml:space=\"preserve\">{text}</t></is></c>");
            }
            sb.Append("</row>");
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string ColumnLetter(int index)
    {
        // 0 -> A, 1 -> B, ..., 25 -> Z, 26 -> AA, ...
        var letters = "";
        index++;
        while (index > 0)
        {
            var rem = (index - 1) % 26;
            letters = (char)('A' + rem) + letters;
            index = (index - 1) / 26;
        }
        return letters;
    }

    private static string EscapeXml(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
