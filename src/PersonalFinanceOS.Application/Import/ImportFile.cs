using System.Text;

namespace PersonalFinanceOS.Application.Import;

/// <summary>
/// A bank file read into rows of text cells, exactly as found. Nothing is interpreted yet, and the
/// file itself is not kept anywhere (spec §8).
/// </summary>
public sealed record ImportFile(
    string FileName,
    string Format,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string? EncodingName,
    char? Delimiter)
{
    /// <summary>Bank exports are small; anything bigger is almost certainly the wrong file.</summary>
    public const int MaxBytes = 20 * 1024 * 1024;

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);

    /// <summary>Reads a CSV or XLSX file. Throws <see cref="Domain.DomainValidationException"/> with a user-facing code.</summary>
    public static ImportFile Read(string fileName, byte[] content)
    {
        if (content.Length == 0)
        {
            throw new Domain.DomainValidationException("import.file.empty");
        }

        if (content.Length > MaxBytes)
        {
            throw new Domain.DomainValidationException("import.file.tooLarge");
        }

        if (XlsxFormat.LooksLikeXlsx(content))
        {
            return new ImportFile(fileName, "xlsx", XlsxFormat.Read(content), null, null);
        }

        if (Path.GetExtension(fileName).Equals(".xls", StringComparison.OrdinalIgnoreCase) || LooksBinary(content))
        {
            // Old .xls, PDF and the like: not supported (spec §8), say so instead of showing garbage.
            throw new Domain.DomainValidationException("import.file.unsupported");
        }

        var (text, encoding) = CsvFormat.Decode(content);
        var delimiter = CsvFormat.DetectDelimiter(text);
        var rows = CsvFormat.Parse(text, delimiter);
        if (rows.Count == 0)
        {
            throw new Domain.DomainValidationException("import.file.empty");
        }

        return new ImportFile(fileName, "csv", rows, encoding.WebName, delimiter);
    }

    /// <summary>Same file, other delimiter, when the user corrects the detected one.</summary>
    public ImportFile WithDelimiter(char delimiter, byte[] content)
    {
        if (Format != "csv")
        {
            return this;
        }

        var (text, _) = CsvFormat.Decode(content);
        return this with { Rows = CsvFormat.Parse(text, delimiter), Delimiter = delimiter };
    }

    private static bool LooksBinary(byte[] content)
    {
        var sample = content.AsSpan(0, Math.Min(content.Length, 4096));
        var zeros = 0;
        foreach (var b in sample)
        {
            if (b == 0)
            {
                zeros++;
            }
        }

        // UTF-16 text has many zeros too, but then it starts with a byte order mark.
        var utf16 = sample.Length >= 2 && ((sample[0] == 0xFF && sample[1] == 0xFE) || (sample[0] == 0xFE && sample[1] == 0xFF));
        return !utf16 && zeros > 0;
    }
}

/// <summary>Reading CSV text: encoding, delimiter and quoting. Deterministic; no culture involved.</summary>
public static class CsvFormat
{
    private static readonly char[] Candidates = [';', ',', '\t', '|'];

    static CsvFormat()
    {
        // Windows-1252 (Belgian bank exports, with the euro sign at 0x80) is not built into .NET.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>UTF-8 (with or without BOM) or UTF-16 when marked; otherwise Windows-1252.</summary>
    public static (string Text, Encoding Encoding) Decode(byte[] content)
    {
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            return (new UTF8Encoding(false).GetString(content, 3, content.Length - 3), Encoding.UTF8);
        }

        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(content, 2, content.Length - 2), Encoding.Unicode);
        }

        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(content, 2, content.Length - 2), Encoding.BigEndianUnicode);
        }

        try
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return (strict.GetString(content), Encoding.UTF8);
        }
        catch (DecoderFallbackException)
        {
            var windows1252 = Encoding.GetEncoding(1252);
            return (windows1252.GetString(content), windows1252);
        }
    }

    /// <summary>
    /// The candidate that splits the most lines into the same number (more than one) of cells.
    /// Quoted text is ignored, so "Supermarkt, Gent" never decides it.
    /// </summary>
    public static char DetectDelimiter(string text)
    {
        var lines = SplitLines(text).Where(l => l.Trim().Length > 0).Take(30).ToList();
        var best = ';';
        var bestScore = -1;
        foreach (var candidate in Candidates)
        {
            var counts = lines.Select(l => CountOutsideQuotes(l, candidate)).Where(c => c > 0).ToList();
            if (counts.Count == 0)
            {
                continue;
            }

            // Lines agreeing on the most common count; ties go to the earlier (more common) candidate.
            var score = counts.GroupBy(c => c).Max(g => g.Count() * 1000 + g.Key);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>RFC 4180 style: quoted cells may contain the delimiter, doubled quotes and line breaks.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text, char delimiter)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var cellStarted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            if (c == '"' && !cellStarted)
            {
                quoted = true;
                cellStarted = true;
            }
            else if (c == delimiter)
            {
                row.Add(cell.ToString().Trim());
                cell.Clear();
                cellStarted = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString().Trim());
                cell.Clear();
                cellStarted = false;
                AddRow(rows, row);
                row = [];
            }
            else
            {
                if (!char.IsWhiteSpace(c))
                {
                    cellStarted = true;
                }

                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString().Trim());
            AddRow(rows, row);
        }

        return rows;
    }

    private static void AddRow(List<IReadOnlyList<string>> rows, List<string> row)
    {
        // Blank lines carry nothing; keeping them would only shift row numbers in messages.
        if (row.Exists(c => c.Length > 0))
        {
            rows.Add(row);
        }
    }

    private static IEnumerable<string> SplitLines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r'));

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        var count = 0;
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == delimiter && !quoted)
            {
                count++;
            }
        }

        return count;
    }
}
