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
        var rawLines = ExtractLineItems(pdf);

        // Canonical grouping key: (NormalizedProject, DescriptionKey, UnitPrice). Adding UnitPrice
        // prevents wrongly merging lines that share a description but were invoiced at different rates
        // (e.g. clear vs satinated variants at 32.00 vs 37.00 €/m²). Duplicate source rows (like April's
        // 3660404-26 printed twice) are preserved by summing — never deduped.
        var grouped = rawLines
            .GroupBy(l => new
            {
                Project = NormalizeProject(l.ExternalCode ?? string.Empty),
                DescKey = NormalizeDescKey(l.DescriptionRaw),
                UnitPrice = l.UnitPrice ?? 0m
            })
            .Select(g =>
            {
                var first = g.First();
                var sumQty = g.Sum(x => x.Qty);
                var sumAmount = g.Sum(x => x.LineTotal);
                return new CanonicalInvoiceLine(
                    CodIntern: null,
                    DescriptionRaw: first.DescriptionRaw,
                    Qty: sumQty,
                    Uom: first.Uom,
                    UnitPrice: first.UnitPrice,
                    LineTotal: sumAmount,
                    Bare: null,
                    ExternalCode: null,
                    PropertyClass: "GEAM");
            })
            .ToList();

        var lineSum = grouped.Sum(l => l.LineTotal);
        var priceQtySum = grouped.Sum(l => (l.UnitPrice ?? 0m) * l.Qty);
        // Subtotal = printed "TOTAL <x> EUR" (pre-discount). GrandTotal = "TOTAL CIP / ROMANIA <x> EUR"
        // (post-discount) if present, otherwise Subtotal − Discount. Reported on the canonical invoice
        // as GrossTotal (i.e. what the supplier actually invoices).
        var subtotal = header.Subtotal;
        var discount = header.Discount ?? 0m;
        var grandTotal = header.GrandTotal ?? (subtotal.HasValue ? subtotal - discount : null);

        decimal confidence = 0.85m;
        var noteList = new List<string>();

        // Check A: unit price × quantity sum matches summed amounts.
        if (lineSum > 0 && Math.Abs(priceQtySum - lineSum) > 0.01m)
        {
            confidence = 0.65m;
            noteList.Add($"Σ(P×Q)={priceQtySum:F2} differs from Σamount={lineSum:F2}");
        }

        // Check B: summed grouped amounts match the printed pre-discount subtotal.
        if (subtotal.HasValue && lineSum > 0 && Math.Abs(lineSum - subtotal.Value) > 0.01m)
        {
            confidence = Math.Min(confidence, 0.60m);
            noteList.Add($"Σamount={lineSum:F2} differs from printed subtotal={subtotal:F2}");
        }

        // Check C: printed subtotal − discount reconciles with the printed grand total.
        if (subtotal.HasValue && header.GrandTotal.HasValue &&
            Math.Abs((subtotal.Value - discount) - header.GrandTotal.Value) > 0.01m)
        {
            confidence = Math.Min(confidence, 0.60m);
            noteList.Add($"Subtotal-Discount ({(subtotal - discount):F2}) differs from grand total {header.GrandTotal:F2}");
        }

        return new CanonicalInvoice(
            Supplier: "Yildiz Cam San. Ve Tic. A.S.",
            InvoiceNo: header.InvoiceNo,
            InvoiceDate: header.InvoiceDate,
            Currency: "EUR",
            NetTotal: grandTotal ?? (lineSum > 0 ? lineSum : subtotal),
            VatTotal: 0m,
            GrossTotal: grandTotal ?? (lineSum > 0 ? lineSum : subtotal),
            Lines: grouped,
            Metadata: new CanonicalMetadata(confidence, "YildizExtractor", noteList.Count > 0 ? string.Join("; ", noteList) : null));
    }

    private static string NormalizeDescKey(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        return Regex.Replace(s.Trim().ToUpperInvariant(), @"\s+", " ");
    }

    /// Normalize project names so variants consolidate into one group:
    ///   • Turkish dotted-İ and dotless-ı → I (VİVALDI → VIVALDI).
    ///   • Hyphens and runs of whitespace → single space (PHASE-1 → PHASE 1).
    private static string NormalizeProject(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var upper = s.Replace('İ', 'I').Replace('ı', 'I').ToUpperInvariant();
        upper = Regex.Replace(upper, @"[\s\-]+", " ").Trim();
        return upper;
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

        // Grand total — the final invoiced figure (post-discount), labeled "TOTAL CIP / <country>".
        var grandMatch = Regex.Match(text, @"TOTAL\s+CIP\s*/\s*\w+[^0-9]*?([\d.,]+)\s*EUR", RegexOptions.IgnoreCase);
        if (grandMatch.Success)
            h.GrandTotal = EuropeanNumberParser.TryParse(grandMatch.Groups[1].Value);

        // Discount line (if any).
        var discountMatch = Regex.Match(text, @"DISCOUNT[^0-9]*?([\d.,]+)\s*EUR", RegexOptions.IgnoreCase);
        if (discountMatch.Success)
            h.Discount = EuropeanNumberParser.TryParse(discountMatch.Groups[1].Value);

        // Pre-discount subtotal — the first "TOTAL <x> EUR" that isn't the "TOTAL CIP" line.
        foreach (Match m in Regex.Matches(text, @"(?<!CIP\s*/\s*\w{0,20}\s{0,10})TOTAL[^0-9\n]*?([\d.,]+)\s*EUR", RegexOptions.IgnoreCase))
        {
            // Ignore the grand-total line we've already captured.
            if (grandMatch.Success && m.Index == grandMatch.Index) continue;
            var val = EuropeanNumberParser.TryParse(m.Groups[1].Value);
            if (val.HasValue && val > (h.Subtotal ?? 0)) h.Subtotal = val;
        }

        // Legacy field — keep populated as the best available "amount" for backwards compat.
        h.GrossTotal = h.GrandTotal ?? h.Subtotal;
        return h;
    }

    // ─── PRODUCTION NO anchors matched to numeric rows by Y proximity ───

    private static readonly Regex ProdNoExact = new(@"^\d{5,8}-\d{1,3}$", RegexOptions.Compiled);
    private static readonly Regex ProdNoInside = new(@"(\d{7}-\d{1,3})", RegexOptions.Compiled);

    // Markers that denote the start of the invoice footer (totals, discount, weights, bank info).
    // Any text row at or below the topmost matching row is excluded from description collection.
    private static readonly Regex FooterStartRegex = new(
        @"^(TOTAL\s|DISCOUNT\s|\*\*\s*Discount|Box\s*:|Total\s+(Sqm|unit)|Net\s+Weight|Gross\s+Weight|Payment\s+Term|Place\s+of\s+Origin|Shipped\s+by|Incoterms|ACCOUNT\s+NAME|Address\s*:|BANK\s*:|BRANCH\s*:|IBAN\s*:|SWIFT|Cash\s+Against)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

            // 2. Group words into text rows.
            // Tolerance 6 merges an "M" main row with its superscript "2" + UP/AMOUNT that sit ~4–5 units below,
            // while still keeping separate invoice items (which are usually ≥8 units apart) in distinct rows.
            var rows = GroupByY(words, 6.0);

            // 3. Build index: for each row, try to parse numeric tail
            var rowData = rows.Select(r => new
            {
                Words = r,
                Tokens = r.Select(w => w.Text).ToList(),
                Y = r[0].BoundingBox.Bottom,
                Parsed = ParseNumericTail(r.Select(w => w.Text).ToList())
            }).ToList();

            // 3a. Find the topmost footer row on this page. Description collection must not cross it.
            double tableEndY = double.NegativeInfinity;
            foreach (var rd in rowData)
            {
                var rowText = string.Join(" ", rd.Tokens).TrimStart();
                if (FooterStartRegex.IsMatch(rowText) && rd.Y > tableEndY)
                    tableEndY = rd.Y;
            }

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
                // Clamp: don't let the last item's window extend into the footer (TOTAL/DISCOUNT/Box/etc.)
                if (tableEndY > descBottom) descBottom = tableEndY;
                var descParts = new List<string>();

                // The row that contains prodNo may differ from the row with the numeric tail:
                //   • April format (compact): prodNo and numeric tail share the row → prodRow == numRow.
                //   • March format (multi-row): prodNo sits with the description on one row, the
                //     numeric tail lives on a separate row below (the "glass code" row).
                // Take the description from the prodRow — everything after prodNo, skipping stray
                // unit-marker tokens. If prodRow == numRow, stop at the numeric tail's start.
                var prodRow = rowData.FirstOrDefault(r =>
                    Math.Abs(r.Y - prodY) < 6 && r.Tokens.Any(t => t.Contains(prodNo)));
                if (prodRow != null)
                {
                    var prodTokenIdx = prodRow.Tokens.FindIndex(t => t.Contains(prodNo));
                    var endIdx = prodRow == numRow ? numRow.Parsed.Value.FirstNumericIndex : prodRow.Tokens.Count;
                    if (endIdx > prodTokenIdx + 1)
                    {
                        var between = prodRow.Tokens.Skip(prodTokenIdx + 1).Take(endIdx - prodTokenIdx - 1)
                            .Where(t => t is not ("M" or "2" or "M2" or "M²"))
                            .ToList();
                        var betweenText = string.Join(" ", between).Trim();
                        if (betweenText.Length > 2) descParts.Add(betweenText);
                    }

                    // When prodRow and numRow are different Y-groups, the description wraps:
                    // numRow carries "GlassCode <rest of the description> <numeric tail>". Pull
                    // the pre-tail slice, strip the leading Glass Code cell with FindGlassSpecStart,
                    // and append — otherwise the second half of long descriptions is lost.
                    if (prodRow != numRow)
                    {
                        var firstNum = numRow.Parsed.Value.FirstNumericIndex;
                        if (firstNum > 0)
                        {
                            var preTail = string.Join(" ", numRow.Tokens.Take(firstNum)).Trim();
                            var specStart2 = FindGlassSpecStart(preTail);
                            if (specStart2 > 0) preTail = preTail[specStart2..].Trim();
                            if (preTail.Length > 2) descParts.Add(preTail);
                        }
                    }
                }

                // From other rows in the Y range (description continuation rows, e.g. trailing "Annealed")
                foreach (var rd in rowData)
                {
                    if (rd == numRow || rd == prodRow) continue;
                    if (rd.Y > descTop || rd.Y < descBottom) continue;
                    if (rd.Parsed != null) continue; // skip other numeric rows
                    var rowText = string.Join(" ", rd.Tokens);
                    // Skip rows that belong to a DIFFERENT prodNo, not our own.
                    if (ProdNoInside.IsMatch(rowText) && !rowText.Contains(prodNo)) continue;
                    if (rowText.Length > 3) descParts.Add(rowText);
                }

                var fullDesc = string.Join(" ", descParts).Trim();
                fullDesc = Regex.Replace(fullDesc, @"\s+", " ");

                // Clean: extract glass spec
                var specStart = FindGlassSpecStart(fullDesc);
                if (specStart > 0)
                    fullDesc = fullDesc[specStart..].Trim();

                // Strip the leading "IGU :" prefix — it's never part of the ERP catalog name
                // and just noise for proposed-item descriptions.
                fullDesc = Regex.Replace(fullDesc, @"^\s*IGU\s*:?\s*", "", RegexOptions.IgnoreCase).Trim();

                // Belt-and-suspenders: if the last item's description accidentally swallowed
                // footer text (TOTAL / DISCOUNT / Box:, etc.), cut it off.
                fullDesc = Regex.Replace(
                    fullDesc,
                    @"\s+M?\s*(TOTAL|DISCOUNT|Incoterms|Box\s*:|Net\s+Weight|Gross\s+Weight|Place\s+of\s+Origin|Shipped\s+by|Payment\s+Term|ACCOUNT\s+NAME|Cash\s+Against).*$",
                    "",
                    RegexOptions.IgnoreCase).Trim();

                // Strip trailing numeric / unit-marker tokens that leaked from the numeric tail
                // (W, H, QTY, PIECE, M²) when the description row also carries partial numeric data.
                var tokensTail = fullDesc.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                while (tokensTail.Count > 0 &&
                       (Regex.IsMatch(tokensTail[^1], @"^\d+([.,]\d+)?$") || tokensTail[^1] is "M" or "2" or "M2" or "M²"))
                {
                    tokensTail.RemoveAt(tokensTail.Count - 1);
                }
                fullDesc = string.Join(" ", tokensTail).Trim().TrimEnd(',', ';');

                if (string.IsNullOrEmpty(fullDesc) || fullDesc.Length < 5)
                    fullDesc = $"Glass item {prodNo}";

                // Project = leftmost tokens in the prod row before the production number
                // (prodRow is either numRow in compact layouts, or a separate row above it).
                List<string> projectTokens = [];
                if (prodRow != null)
                {
                    var pIdx = prodRow.Tokens.FindIndex(t => t.Contains(prodNo));
                    if (pIdx > 0) projectTokens = prodRow.Tokens.Take(pIdx).ToList();
                }
                var project = string.Join(" ", projectTokens).Trim();

                allLines.Add(new CanonicalInvoiceLine(
                    CodIntern: null,
                    DescriptionRaw: fullDesc,
                    Qty: numRow.Parsed.Value.Qty,
                    Uom: "Mp",
                    UnitPrice: numRow.Parsed.Value.UnitPrice,
                    LineTotal: numRow.Parsed.Value.Amount,
                    Bare: null,
                    ExternalCode: project,
                    PropertyClass: "GEAM"));
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

        // Tail layout in the Yildiz PDF (right-to-left):
        //   AMOUNT · UNIT_PRICE · [optional "2" superscript · "M"] · PIECE · QTY(m²) · HEIGHT · WIDTH
        // A "2" alone (without an "M" to its left in the tail) is the PIECE value, not a superscript.
        int idx = tail.Count - 1;
        var amount = EuropeanNumberParser.TryParse(tail[idx--].text) ?? 0m;
        if (amount <= 0) return null;

        var unitPrice = EuropeanNumberParser.TryParse(tail[idx--].text);

        // Consume the superscript "2" only if there's an "M" immediately before it in tail order.
        if (idx >= 0 && tail[idx].text == "2" && idx - 1 >= 0 && tail[idx - 1].text == "M")
            idx--;
        // Consume the "M"/"M²"/"M2" marker.
        if (idx >= 0 && tail[idx].text is "M" or "M²" or "M2")
            idx--;
        if (idx < 0) return null;

        // Skip PIECE (integer count) — we only need QTY in square meters for aggregation.
        idx--;
        if (idx < 0) return null;

        var qty = EuropeanNumberParser.TryParse(tail[idx--].text) ?? 0m;
        if (qty <= 0) return null;

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
        // Each pattern anchors the start of a real glass specification, so any
        // preceding tokens (Glass Code column: "Code:603", "ME 23A/1", "PO 02", ...) get stripped.
        foreach (var p in new[] {
            @"IGU\s*:",              // "IGU : ..."
            @"\b\d{1,2}\.\d\s+\w",   // "88.2 tempered", "44.2 clear"
            @"\b\d{1,2}\s+mm\s+\w",  // "4 mm clear glass", "6 mm sisecam ...", "8 mm ..."
            @"\b\d\s+Cool\s" })
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
        public decimal? Subtotal { get; set; }
        public decimal? Discount { get; set; }
        public decimal? GrandTotal { get; set; }
    }
}
