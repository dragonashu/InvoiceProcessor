using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceProcessor.Web.Contracts;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace InvoiceProcessor.Web.Services.Extraction;

public class CortizoInvoiceExtractor : ISupplierInvoiceExtractor
{
    public string SupplierKey => "cortizo";

    // Column X-boundaries derived from actual Cortizo PDF layout
    private const double ColP_MaxX = 25;
    private const double ColCodRef_MinX = 25;
    private const double ColCodRef_MaxX = 55;
    private const double ColDesc_MinX = 55;
    private const double ColDesc_MaxX = 120;
    private const double ColBare_MinX = 120;
    private const double ColBare_MaxX = 145;
    private const double ColLung_MinX = 145;
    private const double ColLung_MaxX = 178;
    private const double ColFinisaj_MinX = 178;
    private const double ColFinisaj_MaxX = 375;
    private const double ColCantitate_MinX = 375;
    private const double ColCantitate_MaxX = 432;
    private const double ColUnit_MinX = 432;
    private const double ColUnit_MaxX = 458;
    private const double ColPret_MinX = 458;
    private const double ColPret_MaxX = 495;
    private const double ColTotPlata_MinX = 524;

    // Table boundaries
    private const double TableHeaderY = 580;
    private const double TableFooterY = 200;
    private const double NumericYTolerance = 5;

    public bool CanHandle(string rawText)
    {
        var upper = rawText.ToUpperInvariant();
        return upper.Contains("CORTIZO") || upper.Contains("SK2020065685");
    }

    // Summary section coordinates (last page, below table)
    private const double SummaryLabelY = 167;
    private const double SummaryValueY = 148;
    private const double SummaryYTolerance = 5;
    // "VALOARE NETĂ" label at X≈32-90 → value below at X≈40-100
    private const double NetTotalValueMinX = 30;
    private const double NetTotalValueMaxX = 110;
    // "TOTAL FACTURĂ" label at X≈495-560 → value below at X≈490-560
    private const double GrossTotalValueMinX = 490;
    private const double GrossTotalValueMaxX = 560;
    // Currency appears right after gross total
    private const double CurrencyMinX = 540;

    public CanonicalInvoice Extract(PdfDocument pdf, string rawText)
    {
        var header = ExtractHeader(pdf, rawText);
        var lines = ExtractLineItems(pdf);

        var lineTotalSum = lines.Sum(l => l.LineTotal);
        decimal confidence = 0.90m;
        string? notes = null;

        if (header.GrossTotal.HasValue && header.GrossTotal.Value != 0)
        {
            var tolerance = header.GrossTotal.Value * 0.05m;
            if (Math.Abs(lineTotalSum - header.GrossTotal.Value) > tolerance)
            {
                confidence = 0.60m;
                notes = $"Line total sum {lineTotalSum} differs from gross total {header.GrossTotal.Value}";
            }
        }

        return new CanonicalInvoice(
            Supplier: header.SupplierName,
            InvoiceNo: header.InvoiceNo,
            InvoiceDate: header.InvoiceDate,
            Currency: header.Currency ?? "EUR",
            NetTotal: header.NetTotal,
            VatTotal: header.VatTotal,
            GrossTotal: header.GrossTotal,
            Lines: lines,
            Metadata: new CanonicalMetadata(confidence, "CortizoCoordinateExtractor", notes));
    }

    private static HeaderData ExtractHeader(PdfDocument pdf, string text)
    {
        var header = new HeaderData();

        // --- Regex-based fields (work fine on raw text) ---

        // Invoice number: "FACTURA S19 / 001745" or standalone "R21 / 006620"
        var facturaMatch = Regex.Match(text, @"FACTURA\s+([A-Z]\d{2,3}\s*/\s*\d{6})(?!\d)");
        if (!facturaMatch.Success)
            facturaMatch = Regex.Match(text, @"([A-Z]\d{2,3}\s*/\s*\d{6})(?!\d)");
        if (facturaMatch.Success)
            header.InvoiceNo = facturaMatch.Groups[1].Value.Replace(" ", "");

        // Supplier VAT — look for SK or RO VAT on the supplier header line
        var supplierVatMatch = Regex.Match(text, @"\b((?:SK|RO)\d{8,12})\b");
        if (supplierVatMatch.Success)
            header.SupplierVat = supplierVatMatch.Groups[1].Value;

        // --- Coordinate-based fields (from page 1 header and last page summary) ---

        var page1Words = pdf.GetPage(1).GetWords().ToList();

        // Invoice date: value below "DATA" label (label at X≈130, Y≈687 → value at X≈119, Y≈667)
        var dateWord = page1Words
            .Where(w => w.BoundingBox.Left >= 100 && w.BoundingBox.Left < 175 &&
                        w.BoundingBox.Bottom >= 660 && w.BoundingBox.Bottom < 680 &&
                        Regex.IsMatch(w.Text, @"^\d{2}/\d{2}/\d{4}$"))
            .FirstOrDefault();
        if (dateWord != null &&
            DateOnly.TryParseExact(dateWord.Text, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            header.InvoiceDate = date;

        // Supplier name: words at Y≈776 on page 1, after the VAT number and dash
        // Skip the VAT code and dash, capture the company name (e.g. "ALUMINIOS CORTIZO ROMANIA, S.R.L")
        var supplierNameWords = page1Words
            .Where(w => w.BoundingBox.Left >= 67 && w.BoundingBox.Left < 230 &&
                        Math.Abs(w.BoundingBox.Bottom - 776) < 4)
            .OrderBy(w => w.BoundingBox.Left)
            .Select(w => w.Text)
            .Where(t => t != "-") // skip the dash separator
            .ToList();
        if (supplierNameWords.Count > 0)
            header.SupplierName = string.Join(" ", supplierNameWords).TrimEnd(',').Trim();

        // Summary totals from the last page
        var lastPageWords = pdf.GetPage(pdf.NumberOfPages).GetWords().ToList();

        // Net total: value under "VALOARE NETĂ" label (X≈30-110, Y≈147)
        var netWord = lastPageWords
            .Where(w => w.BoundingBox.Left >= NetTotalValueMinX &&
                        w.BoundingBox.Left < NetTotalValueMaxX &&
                        Math.Abs(w.BoundingBox.Bottom - SummaryValueY) < SummaryYTolerance &&
                        Regex.IsMatch(w.Text, @"^[\d.,]+$"))
            .OrderBy(w => w.BoundingBox.Left)
            .FirstOrDefault();
        if (netWord != null)
            header.NetTotal = EuropeanNumberParser.TryParse(netWord.Text);

        // Gross total: value under "TOTAL FACTURĂ" label (X≈490-560, Y≈148)
        var grossWord = lastPageWords
            .Where(w => w.BoundingBox.Left >= GrossTotalValueMinX &&
                        w.BoundingBox.Left < GrossTotalValueMaxX &&
                        Math.Abs(w.BoundingBox.Bottom - SummaryValueY) < SummaryYTolerance &&
                        Regex.IsMatch(w.Text, @"^[\d.,]+$"))
            .FirstOrDefault();
        if (grossWord != null)
            header.GrossTotal = EuropeanNumberParser.TryParse(grossWord.Text);

        // Currency: word right after gross total (X > 540, Y≈148, text matches currency code)
        var currencyWord = lastPageWords
            .Where(w => w.BoundingBox.Left >= CurrencyMinX &&
                        Math.Abs(w.BoundingBox.Bottom - SummaryValueY) < SummaryYTolerance &&
                        Regex.IsMatch(w.Text, @"^(EUR|PLN|RON|USD|GBP)$"))
            .FirstOrDefault();
        if (currencyWord != null)
            header.Currency = currencyWord.Text;

        // VAT total: value in TVA column (X≈380-420, Y≈147)
        var vatWord = lastPageWords
            .Where(w => w.BoundingBox.Left >= 380 && w.BoundingBox.Left < 430 &&
                        Math.Abs(w.BoundingBox.Bottom - SummaryValueY) < SummaryYTolerance &&
                        Regex.IsMatch(w.Text, @"^[\d.,]+$"))
            .FirstOrDefault();
        header.VatTotal = vatWord != null ? EuropeanNumberParser.TryParse(vatWord.Text) : 0m;

        return header;
    }

    private static IReadOnlyList<CanonicalInvoiceLine> ExtractLineItems(PdfDocument pdf)
    {
        var allRows = new List<RawRow>();

        for (int p = 1; p <= pdf.NumberOfPages; p++)
        {
            var page = pdf.GetPage(p);
            var words = page.GetWords().ToList();
            var tableWords = words
                .Where(w => w.BoundingBox.Bottom < TableHeaderY && w.BoundingBox.Bottom > TableFooterY)
                .ToList();

            // The body of the table ends at the first non-item marker
            // ("SUBTOTAL COMANDĂ", "Factura este valabila...", "COD.INTRAST.").
            // Below that line are subtotals and recap tables that must not be
            // parsed as items. See FRA-66317-R11-2088-2026-14 where the
            // COD.INTRAST. recap polluted line 6's description.
            var bodyFooterY = FindBodyFooterY(words);

            // Find line number anchors (P column) above the body footer
            var lineAnchors = FindLineNumberAnchors(tableWords, bodyFooterY);

            foreach (var anchor in lineAnchors)
            {
                var row = ExtractRow(anchor, tableWords, lineAnchors, bodyFooterY);
                if (row != null)
                    allRows.Add(row);
            }
        }

        // Sort by line number and convert to canonical lines
        return allRows
            .OrderBy(r => r.LineNo)
            .Select(r => new CanonicalInvoiceLine(
                CodIntern: r.CodRef,
                DescriptionRaw: r.Description,
                Qty: r.Cantitate,
                Uom: r.Unit ?? "MTS",
                UnitPrice: r.Pret,
                LineTotal: r.TotPlata,
                Bare: r.Bare))
            .ToList();
    }

    // Returns the highest Y of any marker that signals the end of the item
    // body on a Cortizo page. Falls back to TableFooterY if no marker found.
    private static double FindBodyFooterY(List<Word> pageWords)
    {
        var subtotal = pageWords
            .Where(w => w.Text.Equals("SUBTOTAL", StringComparison.OrdinalIgnoreCase))
            .Select(w => (double?)w.BoundingBox.Bottom)
            .DefaultIfEmpty(null)
            .Max();

        var facturaY = pageWords
            .Where(w => w.Text.Equals("Factura", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.BoundingBox.Bottom)
            .Where(y => pageWords.Any(w2 =>
                w2.Text.Equals("valabila", StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(w2.BoundingBox.Bottom - y) < 3))
            .Select(y => (double?)y)
            .DefaultIfEmpty(null)
            .Max();

        var codIntrast = pageWords
            .Where(w => w.Text.StartsWith("COD.INTRAST", StringComparison.OrdinalIgnoreCase))
            .Select(w => (double?)w.BoundingBox.Bottom)
            .DefaultIfEmpty(null)
            .Max();

        double max = TableFooterY;
        if (subtotal.HasValue) max = Math.Max(max, subtotal.Value);
        if (facturaY.HasValue) max = Math.Max(max, facturaY.Value);
        if (codIntrast.HasValue) max = Math.Max(max, codIntrast.Value);
        return max;
    }

    private static List<(int lineNo, double y)> FindLineNumberAnchors(List<Word> tableWords, double bodyFooterY)
    {
        var anchors = new List<(int lineNo, double y)>();

        foreach (var w in tableWords)
        {
            if (w.BoundingBox.Left < ColP_MaxX &&
                w.BoundingBox.Bottom > bodyFooterY &&
                int.TryParse(w.Text, out var lineNo) &&
                lineNo >= 1 && lineNo <= 999)
            {
                anchors.Add((lineNo, w.BoundingBox.Bottom));
            }
        }

        return anchors.OrderByDescending(a => a.y).ToList(); // Top to bottom (decreasing Y)
    }

    private static RawRow? ExtractRow((int lineNo, double y) anchor, List<Word> tableWords, List<(int lineNo, double y)> allAnchors, double bodyFooterY)
    {
        var anchorY = anchor.y;

        // Determine the Y range for this row's description (down to next anchor or body footer)
        var nextAnchorY = allAnchors
            .Where(a => a.y < anchorY - 5)
            .Select(a => (double?)a.y)
            .FirstOrDefault() ?? bodyFooterY;
        var descYBottom = nextAnchorY + 5; // A few points above the next row

        // For numeric columns, find the closest word within tight Y tolerance
        string? codRef = FindWordInColumn(tableWords, ColCodRef_MinX, ColCodRef_MaxX, anchorY, NumericYTolerance);
        string? bare = FindWordInColumn(tableWords, ColBare_MinX, ColBare_MaxX, anchorY, NumericYTolerance);
        string? lung = FindWordInColumn(tableWords, ColLung_MinX, ColLung_MaxX, anchorY, NumericYTolerance);
        string? cantitateStr = FindWordInColumn(tableWords, ColCantitate_MinX, ColCantitate_MaxX, anchorY, NumericYTolerance);
        string? unit = FindWordInColumn(tableWords, ColUnit_MinX, ColUnit_MaxX, anchorY, NumericYTolerance);
        string? pretStr = FindWordInColumn(tableWords, ColPret_MinX, ColPret_MaxX, anchorY, NumericYTolerance);
        string? totPlataStr = FindWordInColumn(tableWords, ColTotPlata_MinX, 600, anchorY, NumericYTolerance);

        // For description, collect all words in the description X range within the full row Y range
        var descWords = tableWords
            .Where(w => w.BoundingBox.Left >= ColDesc_MinX &&
                        w.BoundingBox.Left < ColDesc_MaxX &&
                        w.BoundingBox.Bottom <= anchorY + 2 &&
                        w.BoundingBox.Bottom > descYBottom)
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .ThenBy(w => w.BoundingBox.Left)
            .Select(w => w.Text)
            .ToList();

        var description = string.Join(" ", descWords).Trim();

        // Fallback: TAXA-style rows (e.g. "TAXA VOPSIRE") leave the REF and
        // local-name columns empty — the code and description live in the
        // FINISAJ column instead. Pull from there to avoid an empty desc.
        if (string.IsNullOrEmpty(description))
        {
            var finisajWords = tableWords
                .Where(w => w.BoundingBox.Left >= ColFinisaj_MinX &&
                            w.BoundingBox.Left < ColFinisaj_MaxX &&
                            w.BoundingBox.Bottom <= anchorY + 2 &&
                            w.BoundingBox.Bottom > descYBottom)
                .OrderByDescending(w => w.BoundingBox.Bottom)
                .ThenBy(w => w.BoundingBox.Left)
                .Select(w => w.Text)
                .ToList();
            description = string.Join(" ", finisajWords).Trim();
        }

        if (string.IsNullOrEmpty(description))
            description = $"Line {anchor.lineNo}";

        var cantitate = EuropeanNumberParser.TryParse(cantitateStr) ?? 0m;
        var pret = EuropeanNumberParser.TryParse(pretStr);
        var totPlata = EuropeanNumberParser.TryParse(totPlataStr) ?? 0m;

        return new RawRow
        {
            LineNo = anchor.lineNo,
            CodRef = codRef,
            Description = description,
            Bare = bare,
            Lung = lung,
            Cantitate = cantitate,
            Unit = unit,
            Pret = pret,
            TotPlata = totPlata
        };
    }

    private static string? FindWordInColumn(List<Word> words, double minX, double maxX, double anchorY, double yTolerance)
    {
        return words
            .Where(w => w.BoundingBox.Left >= minX &&
                        w.BoundingBox.Left < maxX &&
                        Math.Abs(w.BoundingBox.Bottom - anchorY) < yTolerance)
            .OrderBy(w => Math.Abs(w.BoundingBox.Bottom - anchorY))
            .Select(w => w.Text)
            .FirstOrDefault();
    }

    private class HeaderData
    {
        public string? InvoiceNo { get; set; }
        public DateOnly? InvoiceDate { get; set; }
        public string? SupplierVat { get; set; }
        public string? SupplierName { get; set; }
        public string? ClientVat { get; set; }
        public string? Currency { get; set; }
        public decimal? NetTotal { get; set; }
        public decimal? VatTotal { get; set; }
        public decimal? GrossTotal { get; set; }
    }

    private class RawRow
    {
        public int LineNo { get; set; }
        public string? CodRef { get; set; }
        public string Description { get; set; } = "";
        public string? Bare { get; set; }
        public string? Lung { get; set; }
        public decimal Cantitate { get; set; }
        public string? Unit { get; set; }
        public decimal? Pret { get; set; }
        public decimal TotPlata { get; set; }
    }
}
