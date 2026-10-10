using System.IO.Compression;
using System.Text;
using PersonalFinanceOS.Application.Import;
using PersonalFinanceOS.Domain;

namespace PersonalFinanceOS.Tests.Application;

/// <summary>Spec §8 steps 2–5: reading CSV/XLSX files, detecting the layout and reading each line.</summary>
public class ImportParsingTests
{
    private static ImportFile Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Import", name);
        return ImportFile.Read(name, File.ReadAllBytes(path));
    }

    private static ImportFile Csv(string text, Encoding? encoding = null) =>
        ImportFile.Read("test.csv", (encoding ?? Encoding.UTF8).GetBytes(text));

    [Fact]
    public void Quoted_cells_keep_delimiters_quotes_and_line_breaks()
    {
        var rows = CsvFormat.Parse("a;\"b;c\";\"say \"\"hi\"\"\"\r\n\"two\nlines\";x;y\r\n\r\n", ';');

        Assert.Equal(2, rows.Count); // blank lines are dropped
        Assert.Equal(["a", "b;c", "say \"hi\""], rows[0]);
        Assert.Equal("two\nlines", rows[1][0]);
    }

    [Theory]
    [InlineData("Datum;Bedrag;Omschrijving\n01/10/2026;-42,80;Supermarkt, Gent\n", ';')]
    [InlineData("Date,Amount,Description\n2026-10-01,-42.80,\"Shop; Ghent\"\n", ',')]
    [InlineData("Date\tAmount\tDescription\n2026-10-01\t-42.80\tShop\n", '\t')]
    public void Delimiter_is_detected_and_commas_in_amounts_do_not_confuse_it(string text, char expected) =>
        Assert.Equal(expected, CsvFormat.DetectDelimiter(text));

    [Fact]
    public void Windows_1252_files_keep_the_euro_sign_and_accents()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // only to write the test file; the app registers it itself
        var bytes = Encoding.GetEncoding(1252).GetBytes("Datum;Bedrag;Omschrijving\n01/10/2026;-42,80;Café € terras\n");
        var file = ImportFile.Read("x.csv", bytes);

        Assert.Equal("windows-1252", file.EncodingName);
        Assert.Equal("Café € terras", file.Rows[1][2]);
    }

    [Fact]
    public void Utf8_bom_is_removed_from_the_first_header()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Datum;Bedrag\n01/10/2026;1,00\n")).ToArray();
        Assert.Equal("Datum", ImportFile.Read("x.csv", bytes).Rows[0][0]);
    }

    [Fact]
    public void Old_excel_files_and_binary_files_are_refused_with_a_clear_reason()
    {
        var ex = Assert.Throws<DomainValidationException>(() => ImportFile.Read("export.xls", [0xD0, 0xCF, 0x11, 0xE0, 0, 0, 0, 1]));
        Assert.Equal("import.file.unsupported", ex.Code);
        Assert.Equal("import.file.empty", Assert.Throws<DomainValidationException>(() => ImportFile.Read("x.csv", [])).Code);
    }

    [Theory]
    [InlineData("-1.234,56", ',', -123_456)]
    [InlineData("1,234.56", '.', 123_456)]
    [InlineData("€ 42,80", ',', 4_280)]
    [InlineData("42,80-", ',', -4_280)]
    [InlineData("(42.80)", '.', -4_280)]
    [InlineData("+5", ',', 500)]
    [InlineData("-0,5", ',', -50)]
    [InlineData("2 400,00 EUR", ',', 240_000)]
    public void Amounts_are_read_exactly_with_the_file_decimal_separator(string text, char separator, long cents) =>
        Assert.Equal(Money.FromCents(cents), ImportValues.ParseAmount(text, separator));

    [Theory]
    [InlineData("42,805", ',')]
    [InlineData("abc", ',')]
    [InlineData("1,2,3", ',')]
    [InlineData("", ',')]
    public void Unreadable_amounts_are_rejected_not_guessed(string text, char separator) =>
        Assert.Null(ImportValues.ParseAmount(text, separator));

    [Theory]
    [InlineData("05/10/2026", "d/M/yyyy", 2026, 10, 5)]
    [InlineData("5-10-2026", "d-M-yyyy", 2026, 10, 5)]
    [InlineData("2026-10-05 00:00:00", "yyyy-M-d", 2026, 10, 5)]
    [InlineData("2026-10-05T12:30:00", "yyyy-M-d", 2026, 10, 5)]
    [InlineData("20261005", "yyyyMMdd", 2026, 10, 5)]
    [InlineData("05/10/26", "d/M/yy", 2026, 10, 5)]
    public void Dates_are_read_in_the_given_format(string text, string format, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), ImportValues.ParseDate(text, format));

    [Fact]
    public void Two_digit_years_never_pass_as_four_digit_years()
    {
        Assert.Null(ImportValues.ParseDate("05/10/26", "d/M/yyyy"));
        Assert.Null(ImportValues.ParseDate("31/09/2026", "d/M/yyyy"));
    }

    [Fact]
    public void Day_first_wins_when_dates_could_be_either_but_month_first_is_recognised_when_needed()
    {
        Assert.Equal("d/M/yyyy", ImportDetector.DateFormat([["05/10/2026"], ["07/10/2026"]], 0));
        Assert.Equal("M/d/yyyy", ImportDetector.DateFormat([["10/05/2026"], ["10/25/2026"]], 0));
    }

    [Fact]
    public void Belgian_semicolon_export_is_mapped_without_help()
    {
        var file = Fixture("kbc-style.csv");
        var m = ImportDetector.Detect(file);
        var header = file.Rows[m.HeaderRow];

        Assert.Equal(';', file.Delimiter);
        Assert.Equal(0, m.HeaderRow);
        Assert.Equal("Datum", header[m.DateColumn]);
        Assert.Equal("d/M/yyyy", m.DateFormat);
        Assert.Equal(AmountLayout.Signed, m.Layout);
        Assert.Equal("Bedrag", header[m.AmountColumn!.Value]); // never "Saldo"
        Assert.Equal(',', m.DecimalSeparator);
        Assert.Equal("Omschrijving", header[m.DescriptionColumn!.Value]);
        Assert.Equal("Naam tegenpartij", header[m.CounterpartyColumn!.Value]);
        Assert.Equal("Munt", header[m.CurrencyColumn!.Value]); // "Valuta" here holds dates
        Assert.Null(m.Problem(file.ColumnCount));

        var rows = ImportRowReader.Read(file, m);
        Assert.Equal(5, rows.Count);
        Assert.Equal(new DateOnly(2026, 10, 3), rows[0].Date);
        Assert.Equal(Money.FromCents(-4_280), rows[0].Amount);
        Assert.Equal(Money.FromCents(240_000), rows[1].Amount);
        Assert.Equal("import.row.currency", rows[3].Problem); // USD is never treated as euros
        Assert.Equal(2, rows[0].RowNumber); // the line number as seen in the file
    }

    [Fact]
    public void Lines_above_the_header_are_skipped_and_bad_dates_reported()
    {
        var file = Fixture("belfius-style.csv");
        var m = ImportDetector.Detect(file);
        var header = file.Rows[m.HeaderRow];

        Assert.Equal(2, m.HeaderRow);
        Assert.Equal("Boekingsdatum", header[m.DateColumn]);
        Assert.Equal("Transactienummer", header[m.ReferenceColumn!.Value]);
        Assert.Equal("Naam tegenpartij bevat", header[m.CounterpartyColumn!.Value]);
        Assert.Equal("Devies", header[m.CurrencyColumn!.Value]);

        var rows = ImportRowReader.Read(file, m);
        Assert.Equal(4, rows.Count);
        Assert.Equal("2026-00101", rows[0].Reference);
        Assert.Equal("import.row.date", rows[3].Problem); // 31 September does not exist
    }

    [Fact]
    public void Positive_amounts_with_an_af_bij_column_get_their_direction_from_it()
    {
        var file = Fixture("ing-style.csv");
        var m = ImportDetector.Detect(file);

        Assert.Equal(',', file.Delimiter);
        Assert.Equal("yyyyMMdd", m.DateFormat);
        Assert.Equal(AmountLayout.WithDirection, m.Layout);
        Assert.Equal(',', m.DecimalSeparator);

        var rows = ImportRowReader.Read(file, m);
        Assert.Equal(Money.FromCents(-4_280), rows[0].Amount);
        Assert.Equal(Money.FromCents(240_000), rows[1].Amount);
        Assert.Equal(Money.FromCents(-123_456), rows[2].Amount);
        Assert.Equal("Albert Heijn 1234", rows[0].Counterparty);
        Assert.Equal("Belasting \"gemeente\" 2026", rows[2].Description);
    }

    [Fact]
    public void Separate_debit_and_credit_columns_are_combined_into_one_signed_amount()
    {
        var file = Fixture("debit-credit-style.csv");
        var m = ImportDetector.Detect(file);

        Assert.Equal(AmountLayout.DebitCredit, m.Layout);
        Assert.Equal('.', m.DecimalSeparator);
        Assert.Equal("TX-1001", ImportRowReader.Read(file, m)[0].Reference);
        Assert.Equal(
            [Money.FromCents(-320), Money.FromCents(240_000), Money.FromCents(-104_280)],
            ImportRowReader.Read(file, m).Select(r => r.Amount!.Value).ToArray());
    }

    [Fact]
    public void A_file_without_header_is_still_mapped_by_its_content()
    {
        var file = Csv("01/10/2026;Supermarkt Gent;-42,80\n02/10/2026;Bakker;-3,20\n03/10/2026;Loon;2400,00\n");
        var m = ImportDetector.Detect(file);

        Assert.Equal(-1, m.HeaderRow);
        Assert.Equal(0, m.DateColumn);
        Assert.Equal(2, m.AmountColumn);
        Assert.Equal(1, m.DescriptionColumn);
        Assert.Equal(3, ImportRowReader.Read(file, m).Count);
    }

    [Fact]
    public void Mapping_survives_a_json_round_trip_and_reports_what_is_missing()
    {
        var m = ImportDetector.Detect(Fixture("kbc-style.csv"));
        Assert.Equal(m, ImportMapping.FromJson(m.ToJson()));
        Assert.Null(ImportMapping.FromJson("{not json"));

        Assert.Equal("import.mapping.amount", (m with { AmountColumn = null }).Problem(11));
        Assert.Equal("import.mapping.column", (m with { DateColumn = 40 }).Problem(11));
        Assert.Equal("import.mapping.description", (m with { DescriptionColumn = null, CounterpartyColumn = null }).Problem(11));
    }

    [Fact]
    public void Xlsx_first_sheet_is_read_with_shared_strings_numbers_and_dates()
    {
        var file = ImportFile.Read("export.xlsx", XlsxBuilder.Build(
            ["Datum", "Omschrijving", "Bedrag"],
            [(new DateOnly(2026, 10, 3), "Supermarkt", -42.8m), (new DateOnly(2026, 10, 5), "Loon", 2400m)]));

        Assert.Equal("xlsx", file.Format);
        Assert.Equal(["Datum", "Omschrijving", "Bedrag"], file.Rows[0]);
        Assert.Equal(["2026-10-03", "Supermarkt", "-42.8"], file.Rows[1]);

        var m = ImportDetector.Detect(file);
        Assert.Equal('.', m.DecimalSeparator);
        Assert.Equal("yyyy-M-d", m.DateFormat);
        Assert.Equal(Money.FromCents(-4_280), ImportRowReader.Read(file, m)[0].Amount);
    }

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("C7", 2)]
    [InlineData("AA3", 26)]
    public void Xlsx_column_letters_become_indexes(string reference, int index) =>
        Assert.Equal(index, XlsxFormat.ColumnIndex(reference, 0));

    [Fact]
    public void Xlsx_serial_dates_account_for_the_missing_1900_leap_day()
    {
        Assert.Equal(new DateOnly(2026, 10, 5), XlsxFormat.SerialToDate(46300m, date1904: false));
        Assert.Equal(new DateOnly(1900, 1, 1), XlsxFormat.SerialToDate(1m, date1904: false));
        Assert.True(XlsxFormat.IsDateFormatCode("dd/mm/yyyy"));
        Assert.False(XlsxFormat.IsDateFormatCode("#,##0.00\" days\""));
    }
}

/// <summary>Writes a minimal but valid .xlsx in memory, so no binary fixture is needed.</summary>
internal static class XlsxBuilder
{
    public static byte[] Build(string[] header, IEnumerable<(DateOnly Date, string Text, decimal Amount)> rows)
    {
        var strings = new List<string>();
        int S(string s)
        {
            var i = strings.IndexOf(s);
            if (i < 0)
            {
                strings.Add(s);
                i = strings.Count - 1;
            }

            return i;
        }

        var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        sheet.Append("<row r=\"1\">");
        for (var c = 0; c < header.Length; c++)
        {
            sheet.Append($"<c r=\"{(char)('A' + c)}1\" t=\"s\"><v>{S(header[c])}</v></c>");
        }

        sheet.Append("</row>");
        var r = 2;
        foreach (var (date, text, amount) in rows)
        {
            var serial = date.DayNumber - new DateOnly(1899, 12, 30).DayNumber;
            sheet.Append($"<row r=\"{r}\"><c r=\"A{r}\" s=\"1\"><v>{serial}</v></c><c r=\"B{r}\" t=\"s\"><v>{S(text)}</v></c>");
            sheet.Append($"<c r=\"C{r}\"><v>{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c></row>");
            r++;
        }

        sheet.Append("</sheetData></worksheet>");

        var shared = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        foreach (var s in strings)
        {
            shared.Append("<si><t>").Append(System.Security.SecurityElement.Escape(s)).Append("</t></si>");
        }

        shared.Append("</sst>");

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }

            Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Blad1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Add("xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy\"/></numFmts><cellXfs count=\"2\"><xf numFmtId=\"0\"/><xf numFmtId=\"164\" applyNumberFormat=\"1\"/></cellXfs></styleSheet>");
            Add("xl/sharedStrings.xml", shared.ToString());
            Add("xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return stream.ToArray();
    }
}
