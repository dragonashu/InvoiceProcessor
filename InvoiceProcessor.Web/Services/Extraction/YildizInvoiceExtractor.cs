using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceProcessor.Web.Contracts;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace InvoiceProcessor.Web.Services.Extraction;

public class YildizInvoiceExtractor : ISupplierInvoiceExtractor
{
    public string SupplierKey => "yildiz";

    public bool CanHandle(string rawText)
    {
        var upper = rawText.ToUpperInvariant();
        return upper.Contains("YILDIZ") || Regex.IsMatch(rawText, @"YCE\d{10,}");
    }

    public CanonicalInvoice Extract(PdfDocument pdf, string rawText)
    {
        var header = ExtractHeader(rawText);
        var lines = ExtractLineItems(pdf);

        var lineSum = lines.Sum(l => l.LineTotal);
        decimal confidence = 0.85m;
        string? notes = null;

        // Use line sum as gross total if header total is missing or suspiciously small
        var grossTotal = header.GrossTotal;
        if (lineSum > 0 && (!grossTotal.HasValue || lineSum > grossTotal * 2))
            grossTotal = lineSum;

        if (grossTotal.HasValue && grossTotal > 0 && header.GrossTotal.HasValue)
        {
            var tolerance = Math.Max(grossTotal.Value * 0.05m, 5.0m);
            if (Math.Abs(lineSum - grossTotal.Value) > tolerance)
            {
                confidence = 0.60m;
                notes = $"Line sum {lineSum:F2} differs from gross {grossTotal:F2}";
            }
        }

        return new CanonicalInvoice(
            Supplier: "Yildiz Cam San. Ve Tic. A.S.",
            InvoiceNo: header.InvoiceNo,
            InvoiceDate: header.InvoiceDate,
            Currency: header.Currency ?? "EUR",
            NetTotal: grossTotal,
            VatTotal: 0m,
            GrossTotal: grossTotal,
            Lines: lines,
            Metadata: new CanonicalMetadata(confidence, "YildizExtractor", notes));
    }

    private static HeaderData ExtractHeader(string text)
    {
        var h = new HeaderData();

        // Invoice number: YCE followed by digits
        var invMatch = Regex.Match(text, @"(YCE\d{10,})");
        if (invMatch.Success)
            h.InvoiceNo = invMatch.Groups[1].Value;

        // Date: look for ISTANBUL/date pattern or near YCE number
        var dateMatch = Regex.Match(text, @"ISTANBUL\s*(\d{1,2}\.\d{1,2}\.\d{4})");
        if (!dateMatch.Success)
            dateMatch = Regex.Match(text, @"(\d{1,2}\.\d{1,2}\.\d{4})");
        if (dateMatch.Success)
        {
            var formats = new[] { "d.M.yyyy", "dd.MM.yyyy", "d.MM.yyyy", "dd.M.yyyy" };
            if (DateOnly.TryParseExact(dateMatch.Groups[1].Value, formats,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                h.InvoiceDate = date;
        }

        // Find gross total - look for the largest EUR/EURO amount
        var eurPatterns = new[] { @"([\d.,]+)\s*EUR(?:O)?", @"TOTAL[^0-9]*?([\d.,]+)" };
        decimal maxAmount = 0;
        foreach (var pat in eurPatterns)
        {
            foreach (Match m in Regex.Matches(text, pat, RegexOptions.IgnoreCase))
            {
                var val = EuropeanNumberParser.TryParse(m.Groups[1].Value);
                if (val.HasValue && val.Value > maxAmount)
                    maxAmount = val.Value;
            }
        }
        if (maxAmount > 0) h.GrossTotal = maxAmount;

        h.Currency = "EUR";
        return h;
    }

    private static IReadOnlyList<CanonicalInvoiceLine> ExtractLineItems(PdfDocument pdf)
    {
        var allLines = new List<CanonicalInvoiceLine>();

        for (int p = 1; p <= pdf.NumberOfPages; p++)
        {
            var page = pdf.GetPage(p);
            var words = page.GetWords().ToList();

            // Group words into rows by Y coordinate (tolerance-based grouping)
            var rows = GroupWordsIntoRows(words, 3.0);

            foreach (var row in rows)
            {
                var line = TryParseGlassRow(row);
                if (line != null)
                    allLines.Add(line);
            }
        }

        return allLines;
    }

    private static List<List<Word>> GroupWordsIntoRows(List<Word> words, double yTolerance)
    {
        if (words.Count == 0) return [];

        var sorted = words.OrderByDescending(w => w.BoundingBox.Bottom).ToList();
        var rows = new List<List<Word>>();
        var currentRow = new List<Word> { sorted[0] };
        var currentY = sorted[0].BoundingBox.Bottom;

        for (int i = 1; i < sorted.Count; i++)
        {
            if (Math.Abs(sorted[i].BoundingBox.Bottom - currentY) < yTolerance)
            {
                currentRow.Add(sorted[i]);
            }
            else
            {
                rows.Add(currentRow.OrderBy(w => w.BoundingBox.Left).ToList());
                currentRow = new List<Word> { sorted[i] };
                currentY = sorted[i].BoundingBox.Bottom;
            }
        }
        if (currentRow.Count > 0)
            rows.Add(currentRow.OrderBy(w => w.BoundingBox.Left).ToList());

        return rows;
    }

    // Pattern: Yildiz glass rows end with numeric columns: W H piece qty M² price amount
    // We parse from RIGHT to LEFT to reliably extract numbers
    private static CanonicalInvoiceLine? TryParseGlassRow(List<Word> row)
    {
        if (row.Count < 6) return null;

        // Collect all words as text tokens, preserving order
        var tokens = row.Select(w => w.Text).ToList();
        var joined = string.Join(" ", tokens);

        // The row must end with a decimal number (the Amount)
        // Pattern from right: {amount} {price} {M²} {qty} {piece} {H} {W} ... {description}
        // M² appears as "M" then "2" in some PDFs, or "M2" or "M²"

        // Try to match the numeric tail of the row
        // Last token should be the amount (decimal)
        // We work backwards from the end
        var numericTail = new List<(string text, int index)>();
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            var t = tokens[i];
            if (Regex.IsMatch(t, @"^\d+[.,]?\d*$") || t == "M" || t == "2" || t == "M2" || t == "M²")
            {
                numericTail.Insert(0, (t, i));
            }
            else
            {
                break;
            }
        }

        // We need at least 7 numeric/unit tokens: W H piece qty M² price amount
        // But M² might be split as "M" "2" or combined as "M2"
        if (numericTail.Count < 7) return null;

        // Parse from the end:
        // amount (last decimal)
        // price (second-to-last decimal)
        // M/M2/2 (unit marker)
        // qty (decimal)
        // piece (integer)
        // H (integer, 3-4 digits)
        // W (integer, 3-4 digits)

        var nums = numericTail.Select(n => n.text).ToList();
        int idx = nums.Count - 1;

        // Amount
        var amountStr = nums[idx--];
        var amount = EuropeanNumberParser.TryParse(amountStr) ?? 0m;
        if (amount <= 0) return null;

        // Unit price
        var priceStr = nums[idx--];
        var unitPrice = EuropeanNumberParser.TryParse(priceStr);

        // Skip M² unit markers
        while (idx >= 0 && (nums[idx] == "M" || nums[idx] == "2" || nums[idx] == "M2" || nums[idx] == "M²"))
            idx--;

        if (idx < 0) return null;

        // Quantity (M²)
        var qtyStr = nums[idx--];
        var qty = EuropeanNumberParser.TryParse(qtyStr) ?? 0m;
        if (qty <= 0) return null;

        // Piece count
        if (idx < 0) return null;
        var pieceStr = nums[idx--];

        // Height (3-4 digit integer)
        if (idx < 0) return null;
        var heightStr = nums[idx--];

        // Width (3-4 digit integer)
        if (idx < 0) return null;
        var widthStr = nums[idx--];

        // Validate: W and H should be 3-4 digit integers (glass dimensions in mm)
        if (!Regex.IsMatch(widthStr, @"^\d{3,4}$") || !Regex.IsMatch(heightStr, @"^\d{3,4}$"))
            return null;

        // Description: everything before the numeric columns
        var descEndIdx = numericTail[0].index;
        // Go back further to include the width position
        var descTokens = tokens.Take(descEndIdx - numericTail.Count + idx + 2).ToList();
        // Actually, take all tokens before the first numeric tail token
        var firstNumIdx = numericTail.Min(n => n.index);
        descTokens = tokens.Take(firstNumIdx).ToList();

        var description = string.Join(" ", descTokens).Trim();
        if (string.IsNullOrEmpty(description)) return null;

        // Strip project name prefix from description (keep glass spec for matching)
        var glassStart = FindGlassDescriptionStart(description);
        if (glassStart > 0)
            description = description[glassStart..].Trim();

        // Skip header/footer/summary rows
        if (description.Contains("TOTAL") || description.Contains("AMOUNT") ||
            description.Contains("Project") || description.Contains("DESCRIPTION"))
            return null;

        return new CanonicalInvoiceLine(
            VendorItemCode: null,
            DescriptionRaw: description,
            Qty: qty,
            Uom: "Mp",
            UnitPrice: unitPrice,
            LineTotal: amount);
    }

    private static int FindGlassDescriptionStart(string desc)
    {
        var patterns = new[]
        {
            @"IGU\s*:",
            @"\b\d{1,2}\.\d\s+\w",
            @"SHAPED\s+IGU",
        };

        foreach (var pattern in patterns)
        {
            var m = Regex.Match(desc, pattern, RegexOptions.IgnoreCase);
            if (m.Success)
                return m.Index;
        }

        return -1;
    }

    private class HeaderData
    {
        public string? InvoiceNo { get; set; }
        public DateOnly? InvoiceDate { get; set; }
        public string? Currency { get; set; }
        public decimal? GrossTotal { get; set; }
    }
}
