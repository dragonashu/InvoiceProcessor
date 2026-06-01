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
        var printedLineCount = FindPrintedLineCount(pdf);

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
            Metadata: new CanonicalMetadata(confidence, "AliplastExtractor", notes),
            ExpectedLineCount: printedLineCount);
    }

    /// The invoice prints a sequential line number in the far-left column of every
    /// item row (1, 2, 3, … up to the last line). The highest such number is the
    /// count of item lines the PDF contains — used to detect dropped lines during
    /// extraction. Returns null when no line-number column is found.
    private static int? FindPrintedLineCount(PdfDocument pdf)
    {
        var max = 0;
        for (int p = 1; p <= pdf.NumberOfPages; p++)
        {
            foreach (var w in pdf.GetPage(p).GetWords())
            {
                // The line-number column sits left of the item-code column (codes start
                // at L≈40). Numbers are 1–3 plain digits, right edge ≤ 40.
                if (w.BoundingBox.Left >= 25 && w.BoundingBox.Right <= 40 &&
                    Regex.IsMatch(w.Text, @"^\d{1,3}$") &&
                    int.TryParse(w.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) &&
                    n > max)
                    max = n;
            }
        }
        return max > 0 ? max : null;
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

        h.Currency = DetectCurrency(text) ?? "EUR";
        return h;
    }

    private static string? DetectCurrency(string text)
    {
        var matches = Regex.Matches(text, @"\b(EUR|RON|USD|PLN|GBP|CHF|HUF|CZK)\b");
        if (matches.Count == 0) return null;
        return matches
            .Select(m => m.Groups[1].Value)
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .First().Key;
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
        // Find leftmost "Quantity" header word (avoids picking up other occurrences)
        var qty = words
            .Where(w => w.Text == "Quantity")
            .OrderBy(w => w.BoundingBox.Left)
            .FirstOrDefault();
        if (qty == null) return null;

        // Find "Amount" header at same Y, to the right
        var amount = words.FirstOrDefault(w =>
            w.Text == "Amount" &&
            Math.Abs(w.BoundingBox.Bottom - qty.BoundingBox.Bottom) < 5 &&
            w.BoundingBox.Left > qty.BoundingBox.Right);
        if (amount == null) return null;

        // Find leftmost "Unit" header between Qty and Amount — the column header,
        // not the "Unit" part of "Unit price"
        var unit = words
            .Where(w => w.Text == "Unit" &&
                        Math.Abs(w.BoundingBox.Bottom - qty.BoundingBox.Bottom) < 5 &&
                        w.BoundingBox.Left > qty.BoundingBox.Left &&
                        w.BoundingBox.Left < amount.BoundingBox.Left)
            .OrderBy(w => w.BoundingBox.Left)
            .FirstOrDefault();

        // Find "VAT" header after Amount
        var vat = words.FirstOrDefault(w =>
            w.Text == "VAT" &&
            Math.Abs(w.BoundingBox.Bottom - qty.BoundingBox.Bottom) < 5 &&
            w.BoundingBox.Left > amount.BoundingBox.Left);

        // The header that bounds the Unit column on the right. In the new format it's "M"
        // (length-in-meters column); in the old format it's the "Unit" of "Unit price".
        double? unitRightBound = null;
        if (unit != null)
        {
            unitRightBound = words
                .Where(w => (w.Text == "M" || w.Text == "Unit") &&
                            Math.Abs(w.BoundingBox.Bottom - qty.BoundingBox.Bottom) < 5 &&
                            w.BoundingBox.Left > unit.BoundingBox.Right &&
                            w.BoundingBox.Left < amount.BoundingBox.Left)
                .OrderBy(w => w.BoundingBox.Left)
                .Select(w => (double?)w.BoundingBox.Left)
                .FirstOrDefault();
        }

        return new ColumnLayout(
            HeaderY: qty.BoundingBox.Bottom,
            ItemMaxX: qty.BoundingBox.Left - 5,
            QtyMinX: qty.BoundingBox.Left - 10,
            QtyMaxX: unit?.BoundingBox.Left - 3 ?? qty.BoundingBox.Right + 25,
            UnitMinX: unit?.BoundingBox.Left - 3 ?? qty.BoundingBox.Right + 5,
            UnitMaxX: unitRightBound - 3 ?? unit?.BoundingBox.Right + 50 ?? qty.BoundingBox.Right + 80,
            AmountMinX: amount.BoundingBox.Left - 20,
            AmountMaxX: vat?.BoundingBox.Left - 3 ?? amount.BoundingBox.Right + 30);
    }

    private record ItemAnchor(string ItemCode, double Y);

    // Regex for Aliplast item codes: starts with letter(s), contains digits.
    // Allows underscores (ACFR135_G), hyphens (ACFX531-500/IN) and multiple slash
    // groups (GT490/AN/7, EF260/MF/6.6).
    private static readonly Regex ItemCodePattern = new(
        @"^[A-Z][A-Z\d._-]+[\dA-Z](?:/[\w.]+)*$", RegexOptions.Compiled);

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
                // Verify: there should be a quantity value at similar Y in the Qty column.
                // Old format always prints decimals (20,00); new format uses plain integers (6, 3).
                var hasQty = tableWords.Any(q =>
                    q.BoundingBox.Left >= cols.QtyMinX &&
                    q.BoundingBox.Left < cols.QtyMaxX &&
                    Math.Abs(q.BoundingBox.Bottom - w.BoundingBox.Bottom) < YTolerance &&
                    Regex.IsMatch(q.Text, @"^\d+(?:[.,]\d+)?$"));

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
        // Amounts can span multiple words when they have thousands separators (e.g. "5 153,20")
        var amount = FindAmountInRange(tableWords, cols.AmountMinX, cols.AmountMaxX, anchorY, YTolerance);

        // If amount not found at item code Y, search below (multi-row items)
        if (amount == 0)
        {
            var nextAnchorY = allAnchors
                .Where(a => a.Y < anchorY - 5)
                .Select(a => (double?)a.Y)
                .FirstOrDefault();
            var searchBottom = nextAnchorY.HasValue ? nextAnchorY.Value + 2 : anchorY - 40;

            var amountWords = tableWords
                .Where(w => w.BoundingBox.Left >= cols.AmountMinX &&
                            w.BoundingBox.Left < cols.AmountMaxX &&
                            w.BoundingBox.Bottom < anchorY - 2 &&
                            w.BoundingBox.Bottom > searchBottom &&
                            Regex.IsMatch(w.Text, @"^\d[\d.,]*$"))
                .OrderByDescending(w => w.BoundingBox.Bottom)
                .ToList();

            if (amountWords.Count > 0)
            {
                // Group words on the same Y and combine
                var topY = amountWords[0].BoundingBox.Bottom;
                var sameRow = amountWords
                    .Where(w => Math.Abs(w.BoundingBox.Bottom - topY) < YTolerance)
                    .OrderBy(w => w.BoundingBox.Left)
                    .Select(w => w.Text);
                amount = EuropeanNumberParser.TryParse(string.Join("", sameRow)) ?? 0m;
            }
        }

        // Description: words in the description area between this item and the next
        var nextAnchorYForDesc = allAnchors
            .Where(a => a.Y < anchorY - 5)
            .Select(a => (double?)a.Y)
            .FirstOrDefault();
        var descYBottom = nextAnchorYForDesc.HasValue ? nextAnchorYForDesc.Value + 3 : anchorY - 50;

        // Cap the description area at the first boundary marker below this line — an
        // order-block boundary ("Order line total" / "Order number") or a page/document
        // footer ("VAT report", "Registers", ...) — so text from the next order block
        // does not leak into the description. This applies to every line, not just the
        // last one on a page: a line that ends an order block has the next block's
        // heading directly beneath it.
        // NOTE: "Commodity" is deliberately NOT a boundary token — each line carries its
        // own "Commodity code:" row on the SAME Y as the variant (I:/E:/L:) row, so
        // capping there would hide the variant info.
        var boundaryTokens = new HashSet<string> { "VAT", "Order", "Report", "Registers", "Payment", "Terms", "Net", "Gross", "Salesman", "Sales" };
        var boundaryTop = tableWords
            .Where(w => w.BoundingBox.Bottom < anchorY - 2 &&
                        w.BoundingBox.Bottom > descYBottom &&
                        boundaryTokens.Contains(w.Text))
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .Select(w => (double?)w.BoundingBox.Bottom)
            .FirstOrDefault();
        if (boundaryTop.HasValue)
            descYBottom = boundaryTop.Value + 3;

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
                        // Exclude commodity-code prefix tokens — old format prints
                        // "Kod PCN/CN: 83024190", new format prints "Commodity code: 83024190".
                        !w.Text.StartsWith("Kod") && w.Text != "PCN/CN:" &&
                        w.Text != "Commodity" && w.Text != "code:" &&
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

        // For FR101 and FR298, append color code from variant line (I: 7016M → FR101/7016M)
        var codIntern = anchor.ItemCode;
        if (codIntern is "FR101" or "FR298")
        {
            var colorCode = FindVariantColor(tableWords, anchorY, descYBottom);
            if (colorCode != null)
                codIntern = $"{codIntern}/{colorCode}";
        }

        // Variant line: description gets "RAL{digits} {L-value}" appended;
        // ExternalCode (API) carries only the digits from the I: value (e.g. "N9006M" → "9006").
        var (descSuffix, apiCode) = ExtractVariantInfo(tableWords, anchorY, descYBottom);
        if (!string.IsNullOrEmpty(descSuffix))
            description = string.IsNullOrEmpty(description) ? descSuffix : $"{description} {descSuffix}";

        // New-item class proposal: a line whose variant row carries an "L:<digits>"
        // length mark is a system profile; everything else is an accessory.
        var propertyClass = HasLengthMark(tableWords, anchorY, descYBottom)
            ? "PROFILE DE SISTEM AL"
            : "ACCESORII DE SISTEM";

        return new CanonicalInvoiceLine(
            CodIntern: codIntern,
            DescriptionRaw: description,
            Qty: qty,
            Uom: NormalizeUnit(unit),
            UnitPrice: qty > 0 ? Math.Round(amount / qty, 4) : null,
            LineTotal: amount,
            ExternalCode: apiCode,
            PropertyClass: propertyClass);
    }

    /// Extracts variant-line info.
    /// Returns (DescriptionSuffix, ApiCode):
    ///   DescriptionSuffix = e.g. "RAL9006 6,500" — appended to the description.
    ///   ApiCode = digits pulled from the I: value (e.g. "N9006M" → "9006"); null if no digits.
    private static (string? DescriptionSuffix, string? ApiCode) ExtractVariantInfo(List<Word> tableWords, double anchorY, double bottomY)
    {
        var variantWords = tableWords
            .Where(w => w.BoundingBox.Bottom < anchorY - 2 &&
                        w.BoundingBox.Bottom > bottomY)
            .ToList();

        var iMarker = variantWords
            .Where(w => w.Text == "I:")
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .FirstOrDefault();
        if (iMarker == null) return (null, null);

        var iValue = variantWords
            .Where(w => Math.Abs(w.BoundingBox.Bottom - iMarker.BoundingBox.Bottom) < 3 &&
                        w.BoundingBox.Left > iMarker.BoundingBox.Right - 1 &&
                        w.BoundingBox.Left < iMarker.BoundingBox.Right + 40 &&
                        w.Text != "I:" &&
                        !w.Text.StartsWith("E:") && !w.Text.StartsWith("L:") &&
                        !Regex.IsMatch(w.Text, @"^[EL]:$"))
            .OrderBy(w => w.BoundingBox.Left)
            .FirstOrDefault();
        if (iValue == null) return (null, null);

        var digitsMatch = Regex.Match(iValue.Text, @"\d+");
        var apiCode = digitsMatch.Success ? digitsMatch.Value : null;

        // Find L: value on the same variant line — may be merged ("L:6,500") or separate ("L:" + "6,500")
        string? lValueText = null;
        var lWord = variantWords
            .Where(w => Math.Abs(w.BoundingBox.Bottom - iMarker.BoundingBox.Bottom) < 3 &&
                        w.Text.StartsWith("L:") && w.Text.Length > 2)
            .FirstOrDefault();

        if (lWord != null)
        {
            lValueText = lWord.Text.Substring(2);
        }
        else
        {
            var lMarker = variantWords
                .Where(w => w.Text == "L:" &&
                            Math.Abs(w.BoundingBox.Bottom - iMarker.BoundingBox.Bottom) < 3)
                .FirstOrDefault();
            if (lMarker != null)
            {
                var lValue = variantWords
                    .Where(w => Math.Abs(w.BoundingBox.Bottom - lMarker.BoundingBox.Bottom) < 3 &&
                                w.BoundingBox.Left > lMarker.BoundingBox.Right - 1 &&
                                w.BoundingBox.Left < lMarker.BoundingBox.Right + 30 &&
                                Regex.IsMatch(w.Text, @"^[\d.,]+$"))
                    .OrderBy(w => w.BoundingBox.Left)
                    .FirstOrDefault();
                if (lValue != null)
                    lValueText = lValue.Text;
            }
        }

        // The RAL colour is appended to the description; the length is appended only
        // next to it. A lone length value (no colour code, e.g. "I: ZWART L:6,600")
        // is dropped — on its own it would just pollute a proposed new item's name.
        string? suffix = null;
        if (apiCode != null)
        {
            suffix = $"RAL{apiCode}";
            if (!string.IsNullOrEmpty(lValueText))
                suffix += $" {lValueText}";
        }

        return (suffix, apiCode);
    }

    /// True when the line's variant row carries an "L:&lt;digits&gt;" length mark
    /// (e.g. L:3,000 / L:6,500). On Aliplast invoices this marks a system profile
    /// (sold by length) as opposed to an accessory. Independent of the I: marker —
    /// some profile lines (e.g. ACVS01) print L: with no colour code.
    private static bool HasLengthMark(List<Word> tableWords, double anchorY, double bottomY)
    {
        var variantWords = tableWords
            .Where(w => w.BoundingBox.Bottom < anchorY - 2 && w.BoundingBox.Bottom > bottomY)
            .ToList();

        // Merged form: "L:6,500"
        if (variantWords.Any(w => Regex.IsMatch(w.Text, @"^L:\s*\d")))
            return true;

        // Separate form: "L:" marker followed by a numeric value on the same row
        foreach (var lMarker in variantWords.Where(w => w.Text == "L:"))
        {
            if (variantWords.Any(w =>
                    Math.Abs(w.BoundingBox.Bottom - lMarker.BoundingBox.Bottom) < 3 &&
                    w.BoundingBox.Left > lMarker.BoundingBox.Right - 1 &&
                    w.BoundingBox.Left < lMarker.BoundingBox.Right + 30 &&
                    Regex.IsMatch(w.Text, @"^\d[\d.,]*$")))
                return true;
        }
        return false;
    }

    /// Finds the color code from the variant line (e.g. "I: 7016M") below an item anchor.
    private static string? FindVariantColor(List<Word> tableWords, double anchorY, double bottomY)
    {
        // Look for "I:" marker below the anchor
        var iMarker = tableWords
            .Where(w => w.Text == "I:" &&
                        w.BoundingBox.Bottom < anchorY - 2 &&
                        w.BoundingBox.Bottom > bottomY)
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .FirstOrDefault();

        if (iMarker == null) return null;

        // The color value is the word immediately to the right of "I:" on the same Y
        var colorWord = tableWords
            .Where(w => Math.Abs(w.BoundingBox.Bottom - iMarker.BoundingBox.Bottom) < 3 &&
                        w.BoundingBox.Left > iMarker.BoundingBox.Right - 1 &&
                        w.BoundingBox.Left < iMarker.BoundingBox.Right + 30 &&
                        w.Text != "I:" &&
                        Regex.IsMatch(w.Text, @"^[A-Z0-9]+$"))
            .OrderBy(w => w.BoundingBox.Left)
            .FirstOrDefault();

        return colorWord?.Text;
    }

    /// Finds an amount value in a column range, combining multi-word amounts like "5 153,20".
    private static decimal FindAmountInRange(List<Word> words, double minX, double maxX, double y, double yTol)
    {
        var candidates = words
            .Where(w => w.BoundingBox.Left >= minX &&
                        w.BoundingBox.Left < maxX &&
                        Math.Abs(w.BoundingBox.Bottom - y) < yTol &&
                        Regex.IsMatch(w.Text, @"^\d[\d.,]*$"))
            .OrderBy(w => w.BoundingBox.Left)
            .Select(w => w.Text)
            .ToList();

        if (candidates.Count == 0) return 0m;

        // Join all number fragments (e.g. ["5", "153,20"] → "5153,20")
        return EuropeanNumberParser.TryParse(string.Join("", candidates)) ?? 0m;
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

    // Unit column "L7", "L6,5", "L4,32" … — an "L" followed by the bar length (in metres).
    // Every such L-length notation marks a profile sold per bar, so the UOM is BARE (rods).
    private static readonly Regex LengthUnitPattern = new(@"^L\s*\d+(?:[.,]\d+)?$", RegexOptions.Compiled);

    private static string? NormalizeUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return unit;
        var u = unit.Trim().ToUpperInvariant();
        if (LengthUnitPattern.IsMatch(u))
            return "BARE";
        return u switch
        {
            "SZT" => "BUC",
            "KPL" => "SET",
            "LM" => "ML",
            "LGT" => "BARE",
            _ => unit
        };
    }

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
