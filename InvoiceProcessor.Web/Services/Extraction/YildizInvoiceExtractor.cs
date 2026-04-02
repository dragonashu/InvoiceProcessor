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
        var grossTotal = lineSum > 0 ? lineSum : header.GrossTotal;

        decimal confidence = 0.85m;
        string? notes = null;
        if (header.GrossTotal.HasValue && header.GrossTotal > 0 && Math.Abs(lineSum - header.GrossTotal.Value) > header.GrossTotal.Value * 0.05m)
        {
            confidence = 0.60m;
            notes = $"Line sum {lineSum:F2} differs from gross {header.GrossTotal:F2}";
        }

        return new CanonicalInvoice(
            Supplier: "Yildiz Cam San. Ve Tic. A.S.",
            InvoiceNo: header.InvoiceNo,
            InvoiceDate: header.InvoiceDate,
            Currency: "EUR",
            NetTotal: grossTotal,
            VatTotal: 0m,
            GrossTotal: grossTotal,
            Lines: lines,
            Metadata: new CanonicalMetadata(confidence, "YildizExtractor", notes));
    }

    private static HeaderData ExtractHeader(string text)
    {
        var h = new HeaderData();
        var invMatch = Regex.Match(text, @"(YCE\d{10,})");
        if (invMatch.Success) h.InvoiceNo = invMatch.Groups[1].Value;

        var dateMatch = Regex.Match(text, @"ISTANBUL\s*(\d{1,2}\.\d{1,2}\.\d{4})");
        if (!dateMatch.Success) dateMatch = Regex.Match(text, @"(\d{1,2}\.\d{1,2}\.\d{4})");
        if (dateMatch.Success)
        {
            if (DateOnly.TryParseExact(dateMatch.Groups[1].Value,
                    ["d.M.yyyy", "dd.MM.yyyy", "d.MM.yyyy", "dd.M.yyyy"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                h.InvoiceDate = date;
        }

        foreach (var pat in new[] { @"TOTAL[^0-9]*?([\d.,]+)\s*EUR", @"([\d.,]+)\s*EUR" })
            foreach (Match m in Regex.Matches(text, pat, RegexOptions.IgnoreCase))
            {
                var val = EuropeanNumberParser.TryParse(m.Groups[1].Value);
                if (val.HasValue && val > (h.GrossTotal ?? 0)) h.GrossTotal = val;
            }
        return h;
    }

    // ─── PRODUCTION NO anchors matched to numeric rows by Y proximity ───

    private static readonly Regex ProdNoExact = new(@"^\d{5,8}-\d{1,3}$", RegexOptions.Compiled);
    private static readonly Regex ProdNoInside = new(@"(\d{7}-\d{1,3})", RegexOptions.Compiled);

    private static IReadOnlyList<CanonicalInvoiceLine> ExtractLineItems(PdfDocument pdf)
    {
        var allLines = new List<CanonicalInvoiceLine>();

        for (int p = 1; p <= pdf.NumberOfPages; p++)
        {
            var words = pdf.GetPage(p).GetWords().ToList();

            // 1. Find all production numbers with Y positions
            var prods = new List<(string No, double Y)>();
            foreach (var w in words)
            {
                if (ProdNoExact.IsMatch(w.Text))
                    prods.Add((w.Text, w.BoundingBox.Bottom));
                else
                {
                    var m = ProdNoInside.Match(w.Text);
                    if (m.Success) prods.Add((m.Groups[1].Value, w.BoundingBox.Bottom));
                }
            }
            prods = prods.OrderByDescending(a => a.Y).ToList();

            // 2. Group words into text rows
            var rows = GroupByY(words, 4.0);

            // 3. Build index: for each row, try to parse numeric tail
            var rowData = rows.Select(r => new
            {
                Words = r,
                Tokens = r.Select(w => w.Text).ToList(),
                Y = r[0].BoundingBox.Bottom,
                Parsed = ParseNumericTail(r.Select(w => w.Text).ToList())
            }).ToList();

            // 4. For each prod anchor, find matching numeric row (within ±10 Y points)
            for (int i = 0; i < prods.Count; i++)
            {
                var (prodNo, prodY) = prods[i];
                var nextProdY = i + 1 < prods.Count ? prods[i + 1].Y : 0.0;

                // Find numeric row closest to this prodNo's Y
                var numRow = rowData
                    .Where(r => r.Parsed != null && Math.Abs(r.Y - prodY) < 10)
                    .OrderBy(r => Math.Abs(r.Y - prodY))
                    .FirstOrDefault();
                if (numRow?.Parsed == null) continue;

                // 5. Collect description from rows between this prod and the next
                double descTop = prodY + 3;
                double descBottom = nextProdY > 0 ? nextProdY + 2 : prodY - 60;
                var descParts = new List<string>();

                // From the numeric row: tokens before the numeric tail, after prodNo
                var prodIdx = numRow.Tokens.FindIndex(t => t.Contains(prodNo));
                if (prodIdx < 0) prodIdx = -1;
                var firstNumIdx = numRow.Parsed.Value.FirstNumericIndex;
                if (firstNumIdx > prodIdx + 1)
                {
                    var between = numRow.Tokens.Skip(prodIdx + 1).Take(firstNumIdx - prodIdx - 1).ToList();
                    var betweenText = string.Join(" ", between);
                    if (betweenText.Length > 2) descParts.Add(betweenText);
                }

                // From other rows in the Y range (description continuation rows)
                foreach (var rd in rowData)
                {
                    if (rd == numRow) continue;
                    if (rd.Y > descTop || rd.Y < descBottom) continue;
                    if (rd.Parsed != null) continue; // skip other numeric rows
                    var rowText = string.Join(" ", rd.Tokens);
                    if (ProdNoInside.IsMatch(rowText)) continue; // skip prod number rows
                    if (rowText.Length > 3) descParts.Add(rowText);
                }

                var fullDesc = string.Join(" ", descParts).Trim();
                fullDesc = Regex.Replace(fullDesc, @"\s+", " ");

                // Clean: extract glass spec
                var specStart = FindGlassSpecStart(fullDesc);
                if (specStart > 0)
                    fullDesc = fullDesc[specStart..].Trim();

                if (string.IsNullOrEmpty(fullDesc) || fullDesc.Length < 5)
                    fullDesc = $"Glass item {prodNo}";

                allLines.Add(new CanonicalInvoiceLine(
                    VendorItemCode: prodNo,
                    DescriptionRaw: fullDesc,
                    Qty: numRow.Parsed.Value.Qty,
                    Uom: "Mp",
                    UnitPrice: numRow.Parsed.Value.UnitPrice,
                    LineTotal: numRow.Parsed.Value.Amount));
            }
        }

        return allLines;
    }

    private record struct NumericTailResult(decimal Amount, decimal? UnitPrice, decimal Qty, int FirstNumericIndex);

    private static NumericTailResult? ParseNumericTail(List<string> tokens)
    {
        if (tokens.Count < 6) return null;

        var tail = new List<(string text, int index)>();
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            var t = tokens[i];
            if (Regex.IsMatch(t, @"^\d+[.,]?\d*$") || t is "M" or "2" or "M2" or "M²")
                tail.Insert(0, (t, i));
            else
                break;
        }

        if (tail.Count < 7) return null;

        int idx = tail.Count - 1;
        var amount = EuropeanNumberParser.TryParse(tail[idx--].text) ?? 0m;
        if (amount <= 0) return null;

        var unitPrice = EuropeanNumberParser.TryParse(tail[idx--].text);

        while (idx >= 0 && tail[idx].text is "M" or "2" or "M2" or "M²") idx--;
        if (idx < 0) return null;

        var qty = EuropeanNumberParser.TryParse(tail[idx--].text) ?? 0m;
        if (qty <= 0) return null;

        if (idx < 0) return null;
        idx--; // piece

        if (idx < 0) return null;
        var h = tail[idx--].text;
        if (!Regex.IsMatch(h, @"^\d{3,4}$")) return null;

        if (idx < 0) return null;
        var w = tail[idx].text;
        if (!Regex.IsMatch(w, @"^\d{3,4}$")) return null;

        return new NumericTailResult(amount, unitPrice, qty, tail[idx].index);
    }

    private static List<List<Word>> GroupByY(List<Word> words, double tolerance)
    {
        if (words.Count == 0) return [];
        var sorted = words.OrderByDescending(w => w.BoundingBox.Bottom).ToList();
        var rows = new List<List<Word>>();
        var current = new List<Word> { sorted[0] };
        var currentY = sorted[0].BoundingBox.Bottom;
        for (int i = 1; i < sorted.Count; i++)
        {
            if (Math.Abs(sorted[i].BoundingBox.Bottom - currentY) < tolerance)
                current.Add(sorted[i]);
            else
            {
                rows.Add(current.OrderBy(w => w.BoundingBox.Left).ToList());
                current = [sorted[i]];
                currentY = sorted[i].BoundingBox.Bottom;
            }
        }
        if (current.Count > 0) rows.Add(current.OrderBy(w => w.BoundingBox.Left).ToList());
        return rows;
    }

    private static int FindGlassSpecStart(string desc)
    {
        foreach (var p in new[] { @"IGU\s*:", @"\b\d{1,2}\.\d\s+\w", @"\b\d\s+Cool\s" })
        {
            var m = Regex.Match(desc, p, RegexOptions.IgnoreCase);
            if (m.Success) return m.Index;
        }
        return -1;
    }

    private class HeaderData
    {
        public string? InvoiceNo { get; set; }
        public DateOnly? InvoiceDate { get; set; }
        public decimal? GrossTotal { get; set; }
    }
}
