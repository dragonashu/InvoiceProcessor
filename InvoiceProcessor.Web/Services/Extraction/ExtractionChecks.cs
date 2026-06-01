using InvoiceProcessor.Web.Contracts;

namespace InvoiceProcessor.Web.Services.Extraction;

/// Outcome of the pre-transfer extraction sanity checks shown on the Preview page
/// and used to gate the "send to robot" action.
public record ExtractionCheckResult(
    decimal LineSum,
    decimal CompareTotal,
    bool SumMatches,
    int ActualLineCount,
    int? ExpectedLineCount,
    bool CountMatches)
{
    public bool TransferAllowed => SumMatches && CountMatches;
}

/// Lightweight, dependency-free checks run against a canonical invoice before it can
/// be transferred to the robot:
///   1. Sum of line totals matches the invoice total (net, falling back to gross).
///   2. Number of extracted lines matches the line count printed on the PDF.
/// A failure of either blocks the transfer so a mis-extracted invoice never reaches the ERP.
public static class ExtractionChecks
{
    public static ExtractionCheckResult Evaluate(CanonicalInvoice invoice)
    {
        var lineSum = invoice.Lines.Sum(l => l.LineTotal);
        var compareTotal = invoice.NetTotal ?? invoice.GrossTotal ?? 0m;
        var tolerance = Math.Max(compareTotal * 0.01m, 0.5m);
        var sumMatches = compareTotal != 0m && Math.Abs(lineSum - compareTotal) <= tolerance;

        var actual = invoice.Lines.Count;
        // When the extractor could not read a printed line count, the check is not
        // applicable and must not block (e.g. suppliers without a numbered line column).
        var countMatches = !invoice.ExpectedLineCount.HasValue || invoice.ExpectedLineCount.Value == actual;

        return new ExtractionCheckResult(lineSum, compareTotal, sumMatches, actual, invoice.ExpectedLineCount, countMatches);
    }
}
