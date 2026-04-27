using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace InvoiceProcessor.Web.Services.Extraction;

public record CustomsDeclarationData(string? Mrn, string? Lrn, decimal? ExchangeRate, DateOnly? ReleaseDate, string? InvoiceRef);

public static class CustomsDeclarationExtractor
{
    // PdfPig's Text output concatenates adjacent words without spaces, so word boundaries (\b)
    // don't exist between consecutive codes. Patterns avoid \b and use fixed lengths / lookarounds.
    //
    // MRN: 18 chars — 2-digit year, 2-letter country, 14 alphanumerics. Must NOT contain "AG".
    private static readonly Regex MrnPattern = new(@"(\d{2}[A-Z]{2}[A-Z0-9]{14})", RegexOptions.Compiled);
    // LRN: contains "AG" + 3-digit suffix (we keep only the 3 digits).
    private static readonly Regex LrnPattern = new(@"\d{2}[A-Z]{2}\d+AG(\d{3})", RegexOptions.Compiled);
    private static readonly Regex RatePattern = new(@"Cursul\s+de\s+schimb\s*:?\s*(\d+[.,]\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // "Dată liber vamă 24/03/2026" — whitespace before the date is optional (PdfPig glues them together).
    private static readonly Regex ReleaseDatePattern = new(@"D[aă]t[aă]\s*liber\s*vam[aă]\s*(\d{1,2}[./-]\d{1,2}[./-]\d{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InvoiceRefPattern = new(@"N380\s*/\s*([A-Z0-9/\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static CustomsDeclarationData Extract(string pdfPath)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));

        string? mrn = null;
        foreach (Match m in MrnPattern.Matches(text))
        {
            var v = m.Groups[1].Value;
            if (v.Contains("AG")) continue; // that's the LRN
            mrn = v;
            break;
        }

        string? lrn = null;
        var lrnMatch = LrnPattern.Match(text);
        if (lrnMatch.Success) lrn = lrnMatch.Groups[1].Value;

        decimal? rate = null;
        var rateMatch = RatePattern.Match(text);
        if (rateMatch.Success &&
            decimal.TryParse(rateMatch.Groups[1].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var r))
            rate = r;

        DateOnly? releaseDate = null;
        var releaseMatch = ReleaseDatePattern.Match(text);
        if (releaseMatch.Success)
        {
            var raw = releaseMatch.Groups[1].Value.Replace('-', '/').Replace('.', '/');
            if (DateOnly.TryParseExact(raw, ["d/M/yyyy", "dd/MM/yyyy", "d/MM/yyyy", "dd/M/yyyy"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var rd))
                releaseDate = rd;
        }

        string? invRef = null;
        var invMatch = InvoiceRefPattern.Match(text);
        if (invMatch.Success) invRef = invMatch.Groups[1].Value.Trim();

        return new CustomsDeclarationData(mrn, lrn, rate, releaseDate, invRef);
    }
}
