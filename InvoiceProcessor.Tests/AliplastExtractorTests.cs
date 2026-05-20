using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Services.Extraction;
using UglyToad.PdfPig;
using Xunit;

namespace InvoiceProcessor.Tests;

public class AliplastExtractorTests
{
    private static CanonicalInvoice Run(string name)
    {
        var pdfPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../samples/", name));
        Assert.True(File.Exists(pdfPath), $"Fixture not found: {pdfPath}");
        using var pdf = PdfDocument.Open(pdfPath);
        var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        return new AliplastInvoiceExtractor().Extract(pdf, text);
    }

    // Old format: header "Quantity Unit Unit price Discount Price aft.dis Amount VAT",
    // quantities printed with decimals ("20,00"), commodity prefix "Kod PCN/CN:".
    [Fact]
    public void OldFormat_HeaderAndTotalsAreExtracted()
    {
        var invoice = Run("factura_Aliplast_fv_2600865.pdf");
        Assert.Equal("2600865", invoice.InvoiceNo);
        Assert.Equal(new DateOnly(2026, 1, 9), invoice.InvoiceDate);
        Assert.Equal("EUR", invoice.Currency);
        Assert.Equal(5049.38m, invoice.GrossTotal);
    }

    [Fact]
    public void OldFormat_AllLinesAreExtracted()
    {
        var invoice = Run("factura_Aliplast_fv_2600865.pdf");
        // Printed item numbers run 1..83 across the two order blocks.
        Assert.Equal(83, invoice.Lines.Count);

        // Order line totals printed on the PDF: 2915,32 + 2134,06 = 5049,38.
        Assert.InRange(invoice.Lines.Sum(l => l.LineTotal), 5049.37m, 5049.39m);
    }

    [Fact]
    public void OldFormat_FirstLineMatchesPrintedRow()
    {
        var invoice = Run("factura_Aliplast_fv_2600865.pdf");
        var line = invoice.Lines.First(l => l.CodIntern == "ACEL011");
        Assert.Equal(20m, line.Qty);
        Assert.Equal("BUC", line.Uom);
        Assert.Equal(32.40m, line.LineTotal);
        Assert.Contains("CORNER WITHOUT CLEAT", line.DescriptionRaw);
    }

    // New format: header "Quantity Unit M Unit price Amount VAT", quantities
    // printed as plain integers ("6"), commodity prefix "Commodity code:".
    [Fact]
    public void NewFormat_HeaderAndTotalsAreExtracted()
    {
        var invoice = Run("fv_2621671.pdf");
        Assert.Equal("2621671", invoice.InvoiceNo);
        Assert.Equal(new DateOnly(2026, 5, 8), invoice.InvoiceDate);
        Assert.Equal("EUR", invoice.Currency);
        Assert.Equal(2653.37m, invoice.GrossTotal);
    }

    [Fact]
    public void NewFormat_AllLinesAreExtracted()
    {
        var invoice = Run("fv_2621671.pdf");
        // Single order block, item numbers 1..67.
        Assert.Equal(67, invoice.Lines.Count);
        Assert.InRange(invoice.Lines.Sum(l => l.LineTotal), 2653.36m, 2653.38m);
    }

    [Fact]
    public void NewFormat_IntegerQuantityRowIsAnchored()
    {
        // ACFX109/IN prints "6 KPL" (no decimal) — the regression we are fixing.
        var invoice = Run("fv_2621671.pdf");
        var line = invoice.Lines.First(l => l.CodIntern == "ACFX109/IN");
        Assert.Equal(6m, line.Qty);
        Assert.Equal("SET", line.Uom);
        Assert.Equal(23.28m, line.LineTotal);
    }

    [Fact]
    public void NewFormat_DescriptionDoesNotLeakCommodityCodePrefix()
    {
        var invoice = Run("fv_2621671.pdf");
        foreach (var line in invoice.Lines)
        {
            Assert.DoesNotContain("Commodity", line.DescriptionRaw);
            Assert.DoesNotContain("code:", line.DescriptionRaw);
        }
    }

    [Fact]
    public void NewFormat_UnitColumnIsNotPolluttedByMLengthColumn()
    {
        // ACUN063 prints "150 LM 150 m" — the unit must be LM (→ ML), not "150" or "m".
        var invoice = Run("fv_2621671.pdf");
        var line = invoice.Lines.First(l => l.CodIntern == "ACUN063");
        Assert.Equal(150m, line.Qty);
        Assert.Equal("ML", line.Uom);
        Assert.Equal(52.50m, line.LineTotal);
    }
}
