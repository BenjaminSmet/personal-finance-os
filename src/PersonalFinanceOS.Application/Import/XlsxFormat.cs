using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace PersonalFinanceOS.Application.Import;

/// <summary>
/// Reads the first worksheet of an .xlsx file as text cells (spec §8 "XLSX if feasible"), without
/// any third-party library: an .xlsx is a zip of XML parts. Numbers come back in invariant form
/// ("-42.8"), cells formatted as dates as "yyyy-MM-dd". Formulas are not evaluated; their last
/// saved value is used. Size limits guard against malformed or hostile files.
/// </summary>
public static class XlsxFormat
{
    private const long MaxPartBytes = 64L * 1024 * 1024;
    private const int MaxRows = 100_000;
    private const int MaxColumns = 200;

    // Built-in Excel number formats that display a date (ECMA-376 §18.8.30).
    private static readonly HashSet<int> BuiltInDateFormats = [14, 15, 16, 17, 22, 27, 30, 36, 50, 57];

    private static readonly XmlReaderSettings Safe = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = false,
        IgnoreComments = true,
    };

    /// <summary>A zip file containing a workbook part.</summary>
    public static bool LooksLikeXlsx(byte[] content)
    {
        if (content.Length < 4 || content[0] != 'P' || content[1] != 'K')
        {
            return false;
        }

        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            return zip.GetEntry("xl/workbook.xml") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static IReadOnlyList<IReadOnlyList<string>> Read(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            var (sheetPath, date1904) = FirstSheet(zip);
            var shared = SharedStrings(zip);
            var dateStyles = DateStyles(zip);
            var entry = zip.GetEntry(sheetPath) ?? throw new Domain.DomainValidationException("import.file.unreadable");
            return Rows(entry, shared, dateStyles, date1904);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or FormatException or OverflowException)
        {
            throw new Domain.DomainValidationException("import.file.unreadable");
        }
    }

    private static (string SheetPath, bool Date1904) FirstSheet(ZipArchive zip)
    {
        string? relationId = null;
        var date1904 = false;
        using (var reader = Open(zip, "xl/workbook.xml"))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.LocalName == "workbookPr")
                {
                    var value = reader.GetAttribute("date1904");
                    date1904 = value is "1" or "true";
                }
                else if (reader.LocalName == "sheet" && relationId is null)
                {
                    relationId = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                }
            }
        }

        if (relationId is not null && zip.GetEntry("xl/_rels/workbook.xml.rels") is not null)
        {
            using var reader = Open(zip, "xl/_rels/workbook.xml.rels");
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" && reader.GetAttribute("Id") == relationId)
                {
                    var target = reader.GetAttribute("Target") ?? string.Empty;
                    var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                    return (path, date1904);
                }
            }
        }

        return ("xl/worksheets/sheet1.xml", date1904);
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is null)
        {
            return list;
        }

        using var reader = Open(zip, "xl/sharedStrings.xml");
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                list.Add(ReadText(reader));
            }
        }

        return list;
    }

    /// <summary>Indexes of cell styles (cellXfs) whose number format shows a date.</summary>
    private static HashSet<int> DateStyles(ZipArchive zip)
    {
        var result = new HashSet<int>();
        if (zip.GetEntry("xl/styles.xml") is null)
        {
            return result;
        }

        var customDateFormats = new HashSet<int>();
        using var reader = Open(zip, "xl/styles.xml");
        var inCellXfs = false;
        var index = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "numFmt")
            {
                var id = int.Parse(reader.GetAttribute("numFmtId") ?? "0", CultureInfo.InvariantCulture);
                if (IsDateFormatCode(reader.GetAttribute("formatCode") ?? string.Empty))
                {
                    customDateFormats.Add(id);
                }
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "cellXfs")
            {
                inCellXfs = !reader.IsEmptyElement;
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs")
            {
                inCellXfs = false;
            }
            else if (inCellXfs && reader.NodeType == XmlNodeType.Element && reader.LocalName == "xf")
            {
                var id = int.Parse(reader.GetAttribute("numFmtId") ?? "0", CultureInfo.InvariantCulture);
                if (BuiltInDateFormats.Contains(id) || customDateFormats.Contains(id))
                {
                    result.Add(index);
                }

                index++;
            }
        }

        return result;
    }

    /// <summary>"dd/mm/yyyy", "d-mmm-yy"… but not "[h]:mm" durations or plain numbers.</summary>
    internal static bool IsDateFormatCode(string code)
    {
        var inQuotes = false;
        var inBrackets = false;
        var hasDay = false;
        var hasYear = false;
        foreach (var c in code.ToLowerInvariant())
        {
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;
                case '[':
                    inBrackets = true;
                    break;
                case ']':
                    inBrackets = false;
                    break;
                case 'd' when !inQuotes && !inBrackets:
                    hasDay = true;
                    break;
                case 'y' when !inQuotes && !inBrackets:
                    hasYear = true;
                    break;
            }
        }

        return hasDay || hasYear;
    }

    private static List<IReadOnlyList<string>> Rows(ZipArchiveEntry entry, List<string> shared, HashSet<int> dateStyles, bool date1904)
    {
        var rows = new List<IReadOnlyList<string>>();
        using var reader = XmlReader.Create(Limited(entry), Safe);
        List<string>? row = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
            {
                row = [];
                if (reader.IsEmptyElement)
                {
                    row = null;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row" && row is not null)
            {
                if (row.Exists(c => c.Length > 0))
                {
                    rows.Add(row);
                    if (rows.Count > MaxRows)
                    {
                        throw new Domain.DomainValidationException("import.file.tooLarge");
                    }
                }

                row = null;
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c" && row is not null)
            {
                var column = ColumnIndex(reader.GetAttribute("r"), row.Count);
                if (column >= MaxColumns)
                {
                    using (reader.ReadSubtree())
                    {
                    }

                    continue;
                }

                var type = reader.GetAttribute("t");
                var style = int.TryParse(reader.GetAttribute("s"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : -1;
                var value = reader.IsEmptyElement ? string.Empty : CellValue(reader, type, style, shared, dateStyles, date1904);
                while (row.Count < column)
                {
                    row.Add(string.Empty);
                }

                row.Add(value.Trim());
            }
        }

        return rows;
    }

    private static string CellValue(XmlReader reader, string? type, int style, List<string> shared, HashSet<int> dateStyles, bool date1904)
    {
        string raw = string.Empty;
        string? inline = null;

        // A subtree reader leaves the outer reader on the cell's end tag, so no sibling is skipped.
        using (var cell = reader.ReadSubtree())
        {
            cell.Read();
            while (!cell.EOF)
            {
                if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "v")
                {
                    raw = cell.ReadElementContentAsString();
                }
                else if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "is")
                {
                    inline = ReadText(cell);
                    cell.Read(); // ReadText stops on the end tag
                }
                else
                {
                    cell.Read();
                }
            }
        }

        switch (type)
        {
            case "s":
                var index = int.Parse(raw, NumberStyles.None, CultureInfo.InvariantCulture);
                return index < shared.Count ? shared[index] : string.Empty;
            case "inlineStr":
                return inline ?? string.Empty;
            case "str" or "e":
                return raw;
            case "b":
                return raw == "1" ? "TRUE" : "FALSE";
            default:
                if (raw.Length == 0)
                {
                    return string.Empty;
                }

                var number = decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (dateStyles.Contains(style))
                {
                    return SerialToDate(number, date1904).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                // Binary floating point noise ("42.799999999999997") must not reach the amount parser.
                return Math.Round(number, 10).ToString("0.##########", CultureInfo.InvariantCulture);
        }
    }

    internal static DateOnly SerialToDate(decimal serial, bool date1904)
    {
        var days = (int)Math.Floor(serial);
        if (date1904)
        {
            return new DateOnly(1904, 1, 1).AddDays(days);
        }

        // Excel counts 1900-02-29, which never existed; serials after it are one day ahead.
        return days < 60 ? new DateOnly(1899, 12, 31).AddDays(days) : new DateOnly(1899, 12, 30).AddDays(days);
    }

    /// <summary>"C7" → 2. Cells without a reference follow the previous one.</summary>
    internal static int ColumnIndex(string? reference, int next)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return next;
        }

        var index = 0;
        foreach (var c in reference)
        {
            if (c is >= 'A' and <= 'Z')
            {
                index = (index * 26) + (c - 'A' + 1);
                if (index > MaxColumns + 1)
                {
                    return MaxColumns;
                }
            }
            else
            {
                break;
            }
        }

        return index == 0 ? next : index - 1;
    }

    /// <summary>
    /// Concatenates all &lt;t&gt; text under the current element (rich text has several runs). Leaves the
    /// reader on that element's end tag (or on the element itself when it is empty).
    /// </summary>
    private static string ReadText(XmlReader reader)
    {
        var text = new System.Text.StringBuilder();
        using (var sub = reader.ReadSubtree())
        {
            sub.Read();
            while (!sub.EOF)
            {
                if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "t")
                {
                    text.Append(sub.ReadElementContentAsString());
                }
                else
                {
                    sub.Read();
                }
            }
        }

        return text.ToString();
    }

    private static XmlReader Open(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? throw new Domain.DomainValidationException("import.file.unreadable");
        return XmlReader.Create(Limited(entry), Safe);
    }

    private static Stream Limited(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxPartBytes)
        {
            throw new Domain.DomainValidationException("import.file.tooLarge");
        }

        // Copy with a hard cap: the size in the zip directory can lie.
        var buffer = new MemoryStream();
        using var source = entry.Open();
        var chunk = new byte[81920];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxPartBytes)
            {
                throw new Domain.DomainValidationException("import.file.tooLarge");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }
}
