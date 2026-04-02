using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceProcessor.Web.Contracts;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace InvoiceProcessor.Web.Services.Extraction;

public class AliplastInvoiceExtractor : ISupplierInvoiceExtractor
{
    public string SupplierKey => "aliplast";

    private const double YTolerance = 5;

    public bool CanHandle(string rawText)
    {
        var upper = rawText.ToUpperInvariant();
        return upper.Contains("ALIPLAST") || upper.Contains("PL9462354607");
    }

    public CanonicalInvoice Extract(PdfDocument pdf, string rawText)
    {
        var header = ExtractHeader(pdf, rawText);
        var lines = ExtractLineItems(pdf);

        var lineSum = lines.Sum(l => l.LineTotal);
        decimal confidence = 0.85m;
        string? notes = null;

        if (header.GrossTotal.HasValue && header.GrossTotal > 0)
        {
            var tolerance = Math.Max(header.GrossTotal.Value * 0.05m, 1.0m);
            if (Math.Abs(lineSum - header.GrossTotal.Value) > tolerance)
            {
                confidence = 0.60m;
                notes = $"Line sum {lineSum:F2} differs from gross {header.GrossTotal:F2}";
            }
        }

        return new CanonicalInvoice(
            Supplier: "Aliplast Sp. z o.o.",
            InvoiceNo: header.InvoiceNo,
            InvoiceDate: header.InvoiceDate,
            Currency: header.Currency ?? "EUR",
            NetTotal: header.NetTotal,
            VatTotal: header.VatTotal,
            GrossTotal: header.GrossTotal,
            Lines: lines,
            Metadata: new CanonicalMetadata(confidence, "AliplastExtractor", notes));
    }

    private static HeaderData ExtractHeader(PdfDocument pdf, string text)
    {
        var h = new HeaderData();

        // Invoice number: appears near "INVOICE" or "BUYER" label (text may be merged without spaces)
        var invoiceMatch = Regex.Match(text, @"(?:INVOICE|BUYER)\s*(\d{5,10})");
        if (!invoiceMatch.Success)
            invoiceMatch = Regex.Match(text, @"(\d{7})(?=Customer|customer)");
        if (invoiceMatch.Success)
            h.InvoiceNo = invoiceMatch.Groups[1].Value;

        // Date: dd.MM.yyyy
        var dateMatch = Regex.Match(text, @"(\d{2}\.\d{2}\.\d{4})");
        if (dateMatch.Success && DateOnly.TryParseExact(dateMatch.Groups[1].Value, "dd.MM.yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            h.InvoiceDate = date;

        // Totals: use multiple strategies since PdfPig may merge text without spaces
        // Strategy 1: Regex with possible merged text
        var grossMatch = Regex.Match(text, @"Gross\s*[Aa]mount:?\s*(\d[\d\s.,]*\d)\s*EUR", RegexOptions.IgnoreCase);
        if (grossMatch.Success)
            h.GrossTotal = EuropeanNumberParser.TryParse(grossMatch.Groups[1].Value.Replace(" ", ""));

        // Strategy 2: Look for "X,XX EUR" pattern preceded by amounts (from VAT report line)
        if (!h.GrossTotal.HasValue)
        {
            var eurAmounts = Regex.Matches(text, @"(\d[\d.,]+)\s*EUR");
            foreach (Match m in eurAmounts)
            {
                var val = EuropeanNumberParser.TryParse(m.Groups[1].Value);
                if (val.HasValue && val > (h.GrossTotal ?? 0))
                    h.GrossTotal = val;
            }
        }

        // Strategy 3: coordinate-based on last page
        if (!h.GrossTotal.HasValue)
        {
            var lastPage = pdf.GetPage(pdf.NumberOfPages);
            var lastWords = lastPage.GetWords().ToList();
            var grossLabel = lastWords.FirstOrDefault(w =>
                w.Text.Equals("Gross", StringComparison.OrdinalIgnoreCase));
            if (grossLabel != null)
            {
                var grossVal = lastWords
                    .Where(w => Math.Abs(w.BoundingBox.Left - grossLabel.BoundingBox.Left) < 60 &&
                                w.BoundingBox.Bottom < grossLabel.BoundingBox.Bottom - 3 &&
                                w.BoundingBox.Bottom > grossLabel.BoundingBox.Bottom - 50 &&
                                Regex.IsMatch(w.Text, @"^\d[\d.,]+$"))
                    .OrderByDescending(w => w.BoundingBox.Bottom)
                    .FirstOrDefault();
                if (grossVal != null)
                    h.GrossTotal = EuropeanNumberParser.TryParse(grossVal.Text);
            }
        }

        h.NetTotal = h.GrossTotal; // For 0% VAT invoices, net = gross
        h.VatTotal = 0m;

        h.Currency = "EUR";
        return h;
    }

    private static IReadOnlyList<CanonicalInvoiceLine> ExtractLineItems(PdfDocument pdf)
    {
        // Find column layout from first page header
        var page1Words = pdf.GetPage(1).GetWords().ToList();
        var cols = FindColumnLayout(page1Words);
        if (cols == null) return [];

        var allLines = new List<CanonicalInvoiceLine>();

        for (int p = 1; p <= pdf.NumberOfPages; p++)
        {
            var words = pdf.GetPage(p).GetWords().ToList();
            var pageCols = FindColumnLayout(words) ?? cols;

            // Filter to table area only (below header, above footer)
            var tableWords = words
                .Where(w => w.BoundingBox.Bottom < pageCols.HeaderY - 3 &&
                            w.BoundingBox.Bottom > 40)
                .ToList();

            var itemAnchors = FindItemAnchors(tableWords, pageCols);
            foreach (var anchor in itemAnchors)
            {
                var line = ExtractLineFromAnchor(tableWords, anchor, pageCols, itemAnchors);
                if (line != null)
                    allLines.Add(line);
            }
        }

        return allLines;
    }

    private record ColumnLayout(
        double HeaderY,
        double ItemMaxX,
        double QtyMinX, double QtyMaxX,
        double UnitMinX, double UnitMaxX,
        double AmountMinX, double AmountMaxX);

    private static ColumnLayout? FindColumnLayout(List<Word> words)
    {
        // Find "Quantity" header word
        var qty = words.FirstOrDefault(w => w.Text == "Quantity");
        if (qty == null) return null;

        // Find "Amount" header at same Y, to the right
        var amount = words.FirstOrDefault(w =>
            w.Text == "Amount" &&
            Math.Abs(w.BoundingBox.Bottom - qty.BoundingBox.Bottom) < 5 &&
            w.BoundingBox.Left > qty.BoundingBox.Right);
        if (amount == null) return null;

        // Find "Unit" header between Qty and Amount
        var unit = words.FirstOrDefault(w =>
            w.Text == "Unit" &&
            Math.Abs(w.BoundingBox.Bottom - qty.BoundingBox.Bottom) < 5 &&
            w.BoundingBox.Left > qty.BoundingBox.Left &&
            w.BoundingBox.Left < amount.BoundingBox.Left);

        // Find "VAT" header after Amount
        var vat = words.FirstOrDefault(w =>
            w.Text == "VAT" &&
            Math.Abs(w.BoundingBox.Bottom - qty.BoundingBox.Bottom) < 5 &&
            w.BoundingBox.Left > amount.BoundingBox.Left);

        return new ColumnLayout(
            HeaderY: qty.BoundingBox.Bottom,
            ItemMaxX: qty.BoundingBox.Left - 5,
            QtyMinX: qty.BoundingBox.Left - 10,
            QtyMaxX: unit?.BoundingBox.Left - 3 ?? qty.BoundingBox.Right + 25,
            UnitMinX: unit?.BoundingBox.Left - 3 ?? qty.BoundingBox.Right + 5,
            UnitMaxX: unit?.BoundingBox.Right + 50 ?? qty.BoundingBox.Right + 80,
            AmountMinX: amount.BoundingBox.Left - 20,
            AmountMaxX: vat?.BoundingBox.Left - 3 ?? amount.BoundingBox.Right + 30);
    }

    private record ItemAnchor(string ItemCode, double Y);

    // Regex for Aliplast item codes: starts with letter(s), contains digits
    private static readonly Regex ItemCodePattern = new(
        @"^[A-Z][A-Z\d.]+[\dA-Z](?:/[\w]+)?$", RegexOptions.Compiled);

    private static List<ItemAnchor> FindItemAnchors(List<Word> tableWords, ColumnLayout cols)
    {
        var anchors = new List<ItemAnchor>();

        foreach (var w in tableWords)
        {
            // Item codes appear in left portion of the Item column
            if (w.BoundingBox.Left < cols.ItemMaxX * 0.5 &&
                w.BoundingBox.Left > 25 &&
                w.Text.Length >= 3 &&
                w.Text.Any(char.IsDigit) &&
                w.Text.Any(char.IsLetter) &&
                ItemCodePattern.IsMatch(w.Text))
            {
                // Verify: there should be a quantity value at similar Y in the Qty column
                var hasQty = tableWords.Any(q =>
                    q.BoundingBox.Left >= cols.QtyMinX &&
                    q.BoundingBox.Left < cols.QtyMaxX &&
                    Math.Abs(q.BoundingBox.Bottom - w.BoundingBox.Bottom) < YTolerance &&
                    Regex.IsMatch(q.Text, @"^\d+[.,]\d+$"));

                if (hasQty)
                    anchors.Add(new ItemAnchor(w.Text, w.BoundingBox.Bottom));
            }
        }

        return anchors.OrderByDescending(a => a.Y).ToList();
    }

    private static CanonicalInvoiceLine? ExtractLineFromAnchor(
        List<Word> tableWords, ItemAnchor anchor, ColumnLayout cols, List<ItemAnchor> allAnchors)
    {
        var anchorY = anchor.Y;

        // Quantity
        var qtyStr = FindWordInRange(tableWords, cols.QtyMinX, cols.QtyMaxX, anchorY, YTolerance);
        var qty = EuropeanNumberParser.TryParse(qtyStr) ?? 0m;

        // Unit (SZT, KPL, LM, LGT)
        var unit = FindWordInRange(tableWords, cols.UnitMinX, cols.UnitMaxX, anchorY, YTolerance);

        // Amount - first try same Y as item code
        var amountStr = FindWordInRange(tableWords, cols.AmountMinX, cols.AmountMaxX, anchorY, YTolerance);
        var amount = EuropeanNumberParser.TryParse(amountStr) ?? 0m;

        // If amount not found at item code Y, search below (multi-row items)
        if (amount == 0)
        {
            var nextAnchorY = allAnchors
                .Where(a => a.Y < anchorY - 5)
                .Select(a => (double?)a.Y)
                .FirstOrDefault();
            var searchBottom = nextAnchorY.HasValue ? nextAnchorY.Value + 2 : anchorY - 40;

            var amountWord = tableWords
                .Where(w => w.BoundingBox.Left >= cols.AmountMinX &&
                            w.BoundingBox.Left < cols.AmountMaxX &&
                            w.BoundingBox.Bottom < anchorY - 2 &&
                            w.BoundingBox.Bottom > searchBottom &&
                            Regex.IsMatch(w.Text, @"^\d+[.,]\d+$"))
                .OrderByDescending(w => w.BoundingBox.Bottom)
                .FirstOrDefault();

            if (amountWord != null)
                amount = EuropeanNumberParser.TryParse(amountWord.Text) ?? 0m;
        }

        // Description: words in the description area between this item and the next
        var nextAnchorYForDesc = allAnchors
            .Where(a => a.Y < anchorY - 5)
            .Select(a => (double?)a.Y)
            .FirstOrDefault();
        var descYBottom = nextAnchorYForDesc.HasValue ? nextAnchorYForDesc.Value + 3 : anchorY - 50;

        var descWords = tableWords
            .Where(w => w.BoundingBox.Left >= 25 &&
                        w.BoundingBox.Left < cols.ItemMaxX &&
                        w.BoundingBox.Bottom <= anchorY + 2 &&
                        w.BoundingBox.Bottom > descYBottom &&
                        // Exclude item code itself
                        !(Math.Abs(w.BoundingBox.Bottom - anchorY) < 3 && w.Text == anchor.ItemCode) &&
                        // Exclude line numbers (standalone small integers)
                        !(Regex.IsMatch(w.Text, @"^\d{1,3}$") && w.BoundingBox.Left < 40) &&
                        // Exclude order numbers (7-digit)
                        !Regex.IsMatch(w.Text, @"^\d{7}$") &&
                        // Exclude Kod PCN/CN lines and PCN codes (8-digit)
                        !w.Text.StartsWith("Kod") && w.Text != "PCN/CN:" &&
                        !Regex.IsMatch(w.Text, @"^\d{8}$") &&
                        // Exclude variant markers (I:, E:, L:) and their values (N9016M, LAN, MF, ZN, 3000, 9010)
                        !Regex.IsMatch(w.Text, @"^[IEL]:$") &&
                        !IsVariantValue(w, tableWords) &&
                        // Exclude standalone prices in desc column
                        !(Regex.IsMatch(w.Text, @"^\d+[.,]\d+$") && w.BoundingBox.Left > cols.ItemMaxX * 0.3) &&
                        // Exclude percentage
                        !Regex.IsMatch(w.Text, @"^\d+%$"))
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .ThenBy(w => w.BoundingBox.Left)
            .Select(w => w.Text)
            .ToList();

        var description = string.Join(" ", descWords).Trim();
        // Clean up description artifacts
        description = Regex.Replace(description, @"\s+", " ").Trim();
        if (string.IsNullOrEmpty(description))
            description = anchor.ItemCode;

        return new CanonicalInvoiceLine(
            VendorItemCode: anchor.ItemCode,
            DescriptionRaw: description,
            Qty: qty,
            Uom: NormalizeUnit(unit),
            UnitPrice: qty > 0 ? Math.Round(amount / qty, 4) : null,
            LineTotal: amount);
    }

    private static string? FindWordInRange(List<Word> words, double minX, double maxX, double y, double yTol)
    {
        return words
            .Where(w => w.BoundingBox.Left >= minX &&
                        w.BoundingBox.Left < maxX &&
                        Math.Abs(w.BoundingBox.Bottom - y) < yTol)
            .OrderBy(w => Math.Abs(w.BoundingBox.Bottom - y))
            .Select(w => w.Text)
            .FirstOrDefault();
    }

    // Detects words that sit on the same Y as a variant marker (I:, E:, L:)
    private static bool IsVariantValue(Word w, List<Word> allWords)
    {
        return allWords.Any(m =>
            Regex.IsMatch(m.Text, @"^[IEL]:$") &&
            Math.Abs(m.BoundingBox.Bottom - w.BoundingBox.Bottom) < 3 &&
            m.Text != w.Text);
    }

    private static string? NormalizeUnit(string? unit) => unit?.ToUpperInvariant() switch
    {
        "SZT" => "BUC",
        "KPL" => "SET",
        "LM" => "ML",
        "LGT" => "BARE",
        _ => unit
    };

    private class HeaderData
    {
        public string? InvoiceNo { get; set; }
        public DateOnly? InvoiceDate { get; set; }
        public string? Currency { get; set; }
        public decimal? NetTotal { get; set; }
        public decimal? VatTotal { get; set; }
        public decimal? GrossTotal { get; set; }
    }
}
