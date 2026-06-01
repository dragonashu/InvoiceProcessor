using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Services.Extraction;
using Xunit;

namespace InvoiceProcessor.Tests;

public class ExtractionChecksTests
{
    private static CanonicalInvoice Make(decimal? net, decimal lineTotalEach, int lineCount, int? expectedCount)
    {
        var lines = Enumerable.Range(0, lineCount)
            .Select(i => new CanonicalInvoiceLine($"C{i}", "desc", 1, "BUC", lineTotalEach, lineTotalEach))
            .ToList();
        return new CanonicalInvoice("S", "1", null, "EUR", net, 0m, net, lines,
            new CanonicalMetadata(0.9m, "Test"), expectedCount);
    }

    [Fact]
    public void AllChecksPass_TransferAllowed()
    {
        // 183 lines * 10.00 = 1830.00 == net, printed count 183 == 183.
        var inv = Make(net: 1830.00m, lineTotalEach: 10.00m, lineCount: 183, expectedCount: 183);
        var r = ExtractionChecks.Evaluate(inv);
        Assert.True(r.SumMatches);
        Assert.True(r.CountMatches);
        Assert.True(r.TransferAllowed);
    }

    [Fact]
    public void LineCountMismatch_BlocksTransfer()
    {
        // 182 extracted but PDF prints 183 → the dropped-line scenario.
        var inv = Make(net: 1820.00m, lineTotalEach: 10.00m, lineCount: 182, expectedCount: 183);
        var r = ExtractionChecks.Evaluate(inv);
        Assert.False(r.CountMatches);
        Assert.False(r.TransferAllowed);
    }

    [Fact]
    public void TotalMismatchBeyondTolerance_BlocksTransfer()
    {
        // Lines sum to 1000 but the stated net is 2000 → far beyond the 1% tolerance.
        var inv = Make(net: 2000.00m, lineTotalEach: 10.00m, lineCount: 100, expectedCount: 100);
        var r = ExtractionChecks.Evaluate(inv);
        Assert.False(r.SumMatches);
        Assert.False(r.TransferAllowed);
    }

    [Fact]
    public void NoPrintedCount_CountCheckIsNotApplicable()
    {
        // Suppliers without a numbered line column: the count check must not block.
        var inv = Make(net: 100.00m, lineTotalEach: 10.00m, lineCount: 10, expectedCount: null);
        var r = ExtractionChecks.Evaluate(inv);
        Assert.True(r.CountMatches);
        Assert.True(r.TransferAllowed);
    }
}
