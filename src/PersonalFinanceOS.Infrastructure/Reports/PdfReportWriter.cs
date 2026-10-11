using System.Globalization;
using System.Text;
using PersonalFinanceOS.Application.Reports;

namespace PersonalFinanceOS.Infrastructure.Reports;

/// <summary>
/// Writes a <see cref="FormattedReport"/> as a printable PDF (spec §18). It uses only the standard PDF fonts, so the file
/// needs no embedded fonts, no extra library and no network, and opens in any PDF reader. Text is encoded as
/// WinAnsi, which covers the euro sign and the accents of Dutch and English. The page turns to landscape when a table
/// does not fit upright; long text wraps inside its column and numbers never wrap.
/// </summary>
public static class PdfReportWriter
{
    private const double Margin = 40;
    private const double TableFont = 8.5;
    private const double MinTableFont = 6.5;
    private const double CellPadding = 4;

    public static byte[] Write(FormattedReport report)
    {
        var portrait = (Width: 595.0, Height: 842.0);
        var landscape = (Width: 842.0, Height: 595.0);
        var widest = report.Tables.Select(t => NaturalWidth(t, TableFont)).DefaultIfEmpty(0).Max();
        var (pageWidth, pageHeight) = widest > portrait.Width - (2 * Margin) ? landscape : portrait;
        var layout = new Layout(pageWidth, pageHeight, report);
        layout.Header();
        foreach (var note in report.Notes)
        {
            layout.Paragraph(note, Font.Regular, 8.5, Gray.Text);
        }

        foreach (var table in report.Tables)
        {
            layout.Table(table);
        }

        return Assemble(report, layout.Finish(), pageWidth, pageHeight);
    }

    private static double NaturalWidth(FormattedTable table, double size) =>
        Columns(table, size).Sum(c => c.Natural);

    private static List<(double Natural, bool Wraps)> Columns(FormattedTable table, double size)
    {
        var result = new List<(double, bool)>();
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var header = TextWidth(table.Columns[i].Header, Font.Bold, size);
            var cells = table.Rows.Select(r => i < r.Cells.Count ? TextWidth(r.Cells[i], r.Style == RowStyle.Total ? Font.Bold : Font.Regular, size) : 0)
                .DefaultIfEmpty(0).Max();
            var wraps = table.Columns[i].Align == ColumnAlign.Left;

            // Headers of number columns may wrap between words; their numbers never wrap.
            var longestWord = table.Columns[i].Header.Split(' ').Max(w => TextWidth(w, Font.Bold, size));
            var natural = wraps ? Math.Max(header, cells) : Math.Max(cells, longestWord);
            result.Add((natural + (2 * CellPadding), wraps));
        }

        return result;
    }

    // ===== Layout: turns the report into pages of drawing operations =====

    private enum Font
    {
        Regular,
        Bold,
        Italic,
    }

    private static class Gray
    {
        public const double Text = 0;
        public const double Subtle = 0.45;
        public const double Rule = 0.7;
        public const double Fill = 0.92;
    }

    private sealed class Layout(double width, double height, FormattedReport report)
    {
        private readonly List<StringBuilder> _pages = [];
        private StringBuilder _page = null!;
        private double _y;

        private double Left => Margin;

        private double Usable => width - (2 * Margin);

        private double Bottom => Margin + 20;

        public void Header()
        {
            NewPage();
            Line(report.Title, Font.Bold, 16, Gray.Text);
            _y -= 4;
            Line(report.Period, Font.Regular, 9.5, Gray.Text);
            Line(report.Generated, Font.Regular, 9.5, Gray.Subtle);
            _y -= 6;
        }

        public void Paragraph(string text, Font font, double size, double gray)
        {
            foreach (var line in Wrap(text, font, size, Usable))
            {
                Line(line, font, size, gray);
            }

            _y -= 2;
        }

        public void Table(FormattedTable table)
        {
            var size = TableFont;
            var columns = Columns(table, size);
            while (columns.Sum(c => c.Natural) > Usable && size > MinTableFont)
            {
                size -= 0.5;
                columns = Columns(table, size);
            }

            var widths = Fit(columns, Usable);
            var lead = size * 1.25;

            // Keep the title with the header and at least one row.
            Ensure(18 + (lead * 3) + 8);
            _y -= 8;
            Line(table.Title, Font.Bold, 11, Gray.Text);
            _y -= 2;
            DrawHeader(table, widths, size, lead);
            if (table.Rows.Count == 0 && table.EmptyText is { } empty)
            {
                Line(empty, Font.Italic, size, Gray.Subtle);
            }

            foreach (var row in table.Rows)
            {
                var font = row.Style == RowStyle.Total ? Font.Bold : Font.Regular;
                var cells = widths.Select((w, i) => i < row.Cells.Count
                    ? (table.Columns[i].Align == ColumnAlign.Left ? Wrap(row.Cells[i], font, size, w - (2 * CellPadding)) : [row.Cells[i]])
                    : [string.Empty]).ToList();
                var rowHeight = (cells.Max(c => c.Count) * lead) + 3;
                if (_y - rowHeight < Bottom)
                {
                    NewPage();
                    DrawHeader(table, widths, size, lead);
                }

                if (row.Style == RowStyle.Total)
                {
                    Fill(Left, _y - rowHeight, widths.Sum(), rowHeight, Gray.Fill);
                }

                var gray = row.Style == RowStyle.Subtle ? Gray.Subtle : Gray.Text;
                var x = Left;
                for (var i = 0; i < widths.Count; i++)
                {
                    var right = table.Columns[i].Align == ColumnAlign.Right;
                    var y = _y - size - 1;
                    foreach (var text in cells[i])
                    {
                        var tx = right ? x + widths[i] - CellPadding - TextWidth(text, font, size) : x + CellPadding;
                        Text(tx, y, text, font, size, gray);
                        y -= lead;
                    }

                    x += widths[i];
                }

                _y -= rowHeight;
            }

            Rule(Left, _y, widths.Sum(), Gray.Rule);
            _y -= 4;
            if (table.Note is { } note)
            {
                Paragraph(note, Font.Italic, 8, Gray.Subtle);
            }
        }

        public List<StringBuilder> Finish()
        {
            for (var i = 0; i < _pages.Count; i++)
            {
                var footer = string.Format(CultureInfo.InvariantCulture, report.PageFormat, i + 1, _pages.Count);
                var title = report.Title;
                _page = _pages[i];
                Text(Left, Margin - 4, title, Font.Regular, 8, Gray.Subtle);
                Text(width - Margin - TextWidth(footer, Font.Regular, 8), Margin - 4, footer, Font.Regular, 8, Gray.Subtle);
            }

            return _pages;
        }

        private void DrawHeader(FormattedTable table, IReadOnlyList<double> widths, double size, double lead)
        {
            var headers = widths.Select((w, i) => Wrap(table.Columns[i].Header, Font.Bold, size, w - (2 * CellPadding))).ToList();
            var headerHeight = (headers.Max(h => h.Count) * lead) + 4;
            var x = Left;
            for (var i = 0; i < widths.Count; i++)
            {
                var right = table.Columns[i].Align == ColumnAlign.Right;
                var y = _y - size - 1;
                foreach (var text in headers[i])
                {
                    var tx = right ? x + widths[i] - CellPadding - TextWidth(text, Font.Bold, size) : x + CellPadding;
                    Text(tx, y, text, Font.Bold, size, Gray.Text);
                    y -= lead;
                }

                x += widths[i];
            }

            _y -= headerHeight;
            Rule(Left, _y, widths.Sum(), Gray.Text);
            _y -= 1;
        }

        private void Line(string text, Font font, double size, double gray)
        {
            Ensure(size * 1.3);
            _y -= size * 1.3;
            Text(Left, _y + (size * 0.25), text, font, size, gray);
        }

        private void Ensure(double needed)
        {
            if (_y - needed < Bottom)
            {
                NewPage();
            }
        }

        private void NewPage()
        {
            _page = new StringBuilder();
            _pages.Add(_page);
            _y = height - Margin;
        }

        private void Text(double x, double y, string text, Font font, double size, double gray)
        {
            if (text.Length == 0)
            {
                return;
            }

            _page.Append(CultureInfo.InvariantCulture, $"{gray:0.##} g BT /F{(int)font + 1} {size:0.##} Tf {x:0.##} {y:0.##} Td (");
            _page.Append(Escape(text));
            _page.Append(") Tj ET\n");
        }

        private void Rule(double x, double y, double w, double gray) =>
            _page.Append(CultureInfo.InvariantCulture, $"{gray:0.##} G 0.6 w {x:0.##} {y:0.##} m {x + w:0.##} {y:0.##} l S\n");

        private void Fill(double x, double y, double w, double h, double gray) =>
            _page.Append(CultureInfo.InvariantCulture, $"{gray:0.##} g {x:0.##} {y:0.##} {w:0.##} {h:0.##} re f\n");
    }

    /// <summary>Gives number columns their natural width and shares what is left between the text columns.</summary>
    private static List<double> Fit(List<(double Natural, bool Wraps)> columns, double usable)
    {
        var total = columns.Sum(c => c.Natural);
        if (total <= usable)
        {
            return columns.Select(c => c.Natural).ToList();
        }

        var fixedWidth = columns.Where(c => !c.Wraps).Sum(c => c.Natural);
        var wrapping = columns.Where(c => c.Wraps).Sum(c => c.Natural);
        var room = Math.Max(usable - fixedWidth, columns.Count(c => c.Wraps) * 40);
        return columns.Select(c => c.Wraps ? Math.Max(40, c.Natural * room / wrapping) : c.Natural).ToList();
    }

    private static List<string> Wrap(string text, Font font, double size, double width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var current = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (TextWidth(candidate, font, size) <= width + 0.01 || current.Length == 0)
                {
                    current.Clear().Append(candidate);
                    while (TextWidth(current.ToString(), font, size) > width + 0.01 && current.Length > 1)
                    {
                        // A single word longer than the column: break it.
                        var fit = current.Length - 1;
                        while (fit > 1 && TextWidth(current.ToString(0, fit), font, size) > width)
                        {
                            fit--;
                        }

                        lines.Add(current.ToString(0, fit));
                        current.Remove(0, fit);
                    }
                }
                else
                {
                    lines.Add(current.ToString());
                    current.Clear().Append(word);
                }
            }

            lines.Add(current.ToString());
        }

        return lines;
    }

    // ===== Fonts and encoding =====

    // Glyph widths of the standard Helvetica fonts for the characters 32..126, in 1/1000 of the font size (Adobe AFM).
    private static readonly short[] HelveticaWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly short[] HelveticaBoldWidths =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];

    private static double TextWidth(string text, Font font, double size)
    {
        var widths = font == Font.Bold ? HelveticaBoldWidths : HelveticaWidths;
        double total = 0;
        foreach (var c in text)
        {
            total += GlyphWidth(c, widths);
        }

        return total * size / 1000;
    }

    private static double GlyphWidth(char c, short[] widths)
    {
        if (c >= 32 && c <= 126)
        {
            return widths[c - 32];
        }

        // Accented letters are about as wide as their base letter.
        var plain = c.ToString().Normalize(NormalizationForm.FormD)[0];
        if (plain >= 32 && plain <= 126)
        {
            return widths[plain - 32];
        }

        return c switch
        {
            '€' => 556,
            ' ' or ' ' => 278,
            '–' => 556,
            '—' => 1000,
            '…' => 1000,
            '•' => 350,
            _ => 556,
        };
    }

    /// <summary>The WinAnsi byte for a character; characters it cannot show become '?'.</summary>
    private static byte Encode(char c) => c switch
    {
        >= ' ' and <= '~' => (byte)c,
        ' ' or ' ' or ' ' => (byte)' ',
        >= '¡' and <= 'ÿ' => (byte)c,
        '€' => 0x80,
        '…' => 0x85,
        '‘' => 0x91,
        '’' => 0x92,
        '“' => 0x93,
        '”' => 0x94,
        '•' => 0x95,
        '–' => 0x96,
        '—' => 0x97,
        '−' => (byte)'-',
        _ => (byte)'?',
    };

    /// <summary>A PDF string body: brackets and backslashes escaped, everything outside ASCII as an octal code.</summary>
    internal static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var b = Encode(c);
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    sb.Append('\\').Append((char)b);
                    break;
                case < 32 or > 126:
                    sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    break;
                default:
                    sb.Append((char)b);
                    break;
            }
        }

        return sb.ToString();
    }

    // ===== File structure =====

    private static byte[] Assemble(FormattedReport report, List<StringBuilder> pages, double width, double height)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            string.Empty, // pages, filled in below
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Oblique /Encoding /WinAnsiEncoding >>",
            $"<< /Title ({Escape(report.Title)}) /Producer (Personal Finance OS) /Creator (Personal Finance OS) >>",
        };
        var kids = new List<int>();
        foreach (var page in pages)
        {
            var content = page.ToString();
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
            var contentId = objects.Count;
            objects.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width:0.##} {height:0.##}] /Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R >> >> /Contents {contentId} 0 R >>"));
            kids.Add(objects.Count);
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>";

        // Content is ASCII only (Escape writes octal codes), so string length is byte length.
        var file = new StringBuilder("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Length);
            file.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = file.Length;
        file.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            file.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        file.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 6 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(file.ToString());
    }
}
