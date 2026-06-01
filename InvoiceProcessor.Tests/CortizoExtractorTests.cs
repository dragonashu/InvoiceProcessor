using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Services.Extraction;
using UglyToad.PdfPig;
using Xunit;

namespace InvoiceProcessor.Tests;

public class CortizoExtractorTests
{
    private static CanonicalInvoice Run(string name)
    {
        var pdfPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../samples/", name));
        Assert.True(File.Exists(pdfPath), $"Fixture not found: {pdfPath}");
        using var pdf = PdfDocument.Open(pdfPath);
        var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        return new CortizoInvoiceExtractor().Extract(pdf, text);
    }

    // Regression for FRA-66317-R11-2088: line 6 is a "TAXA VOPSIRE" item
    // with no REF or local-name columns; its code/description live in the
    // FINISAJ column. The COD.INTRAST. recap below the items used to bleed
    // into line 6's description ("COD.INTRAST. 76042990").
    [Fact]
    public void R11_2088_TaxaVopsireLineExtractsFinisajDescription()
    {
        var invoice = Run("FRA-66317-R11-2088-2026-14.pdf");

        Assert.Equal(6, invoice.Lines.Count);
        Assert.Equal(1889.09m, invoice.GrossTotal);

        var line6 = invoice.Lines[5];
        Assert.Equal(1m, line6.Qty);
        Assert.Equal("BUC", line6.Uom);
        Assert.Equal(224.00m, line6.LineTotal);
        Assert.Contains("TAXA VOPSIRE", line6.DescriptionRaw);
        Assert.DoesNotContain("COD.INTRAST", line6.DescriptionRaw);
    }

    [Theory]
    [InlineData("FRA-66317-R11-2086-2026-14.pdf", 1, 242.68)]
    [InlineData("FRA-66317-R11-2087-2026-14.pdf", 2, 755.56)]
    [InlineData("FRA-66317-R11-2088-2026-14.pdf", 6, 1889.09)]
    [InlineData("FRA-66317-R11-2090-2026-14.pdf", 6, 805.05)]
    [InlineData("FRA-66317-R11-2091-2026-14.pdf", 3, 1037.72)]
    [InlineData("FRA-66317-R21-2453-2026-14.pdf", 20, 839.35)]
    [InlineData("FRA-66317-R21-2454-2026-14.pdf", 10, 663.76)]
    [InlineData("FRA-66317-R21-2455-2026-14.pdf", 5, 16.70)]
    public void Samples_LineCountAndGrossMatch(string name, int lines, decimal gross)
    {
        var invoice = Run(name);
        Assert.Equal(lines, invoice.Lines.Count);
        Assert.Equal(gross, invoice.GrossTotal);
    }
}
