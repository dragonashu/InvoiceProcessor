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

    // New-item class proposal: a variant-row "L:<digits>" length mark => system
    // profile; anything else => accessory.
    [Fact]
    public void PropertyClass_LengthMarkLines_AreSystemProfiles()
    {
        var invoice = Run("factura_Aliplast_fv_2600865.pdf");

        // FR101 (line 74) — variant row "I: N9016M E:N9016M L:6,500".
        var profileWithColor = invoice.Lines.First(l => l.DescriptionRaw.Contains("FRAME/SASH PROFILE FR90EI"));
        Assert.Equal("PROFILE DE SISTEM AL", profileWithColor.PropertyClass);

        // ACVS01 (line 22) — variant row "L:3,000" with NO I: colour marker.
        var profileNoColor = invoice.Lines.First(l => l.CodIntern == "ACVS01");
        Assert.Equal("PROFILE DE SISTEM AL", profileNoColor.PropertyClass);
    }

    [Fact]
    public void PropertyClass_LinesWithoutLengthMark_AreAccessories()
    {
        var invoice = Run("factura_Aliplast_fv_2600865.pdf");

        // ACEL011 (line 1) — no variant row at all.
        Assert.Equal("ACCESORII DE SISTEM", invoice.Lines.First(l => l.CodIntern == "ACEL011").PropertyClass);

        // ACFA501/LAN (line 4) — variant row "I: LAN", no L: mark.
        Assert.Equal("ACCESORII DE SISTEM", invoice.Lines.First(l => l.CodIntern == "ACFA501/LAN").PropertyClass);

        // Every extracted line is classified into exactly one of the two classes.
        Assert.All(invoice.Lines, l =>
            Assert.Contains(l.PropertyClass, new[] { "PROFILE DE SISTEM AL", "ACCESORII DE SISTEM" }));
    }

    // A line that ends an order block must not absorb the next block's heading;
    // a lone length value (no RAL colour) must not be appended to the description.
    [Fact]
    public void Description_DoesNotBleedIntoNextOrderBlock()
    {
        var invoice = Run("fv_2622984.pdf");

        // EF261/ZWART/6.6 (line 42) ends order block 1607139.
        var line42 = invoice.Lines.First(l => l.CodIntern == "EF261/ZWART/6.6");
        Assert.Equal("GLAZING BEAD BLACK 27.5MM", line42.DescriptionRaw);

        // PVC501/6 (line 1) is the sole line of its order block.
        Assert.Equal("SUBPROFILE PVC", invoice.Lines.First(l => l.CodIntern == "PVC501/6").DescriptionRaw);

        Assert.All(invoice.Lines, l =>
        {
            Assert.DoesNotContain("Order number", l.DescriptionRaw);
            Assert.DoesNotContain("Order line", l.DescriptionRaw);
        });
    }

    // IP054 (line 50) is the last item on its page — its variant row (I:/E:/L:)
    // must still be read for the RAL suffix and the system-profile classification.
    [Fact]
    public void LastItemOnPage_VariantRowIsStillRead()
    {
        var invoice = Run("fv_2622984.pdf");
        var ip054 = invoice.Lines.First(l => l.CodIntern == "IP054");
        Assert.Equal("PROFILE DE SISTEM AL", ip054.PropertyClass);
        Assert.Contains("RAL9006", ip054.DescriptionRaw);
    }

    // Regression: the first item ACFX531-500/IN was dropped because the item-code
    // regex rejected the hyphen. It must now be extracted as line 1.
    [Fact]
    public void HyphenatedItemCode_FirstLineIsExtracted()
    {
        var invoice = Run("fv_2624394.pdf");
        var first = invoice.Lines[0];
        Assert.Equal("ACFX531-500/IN", first.CodIntern);
        Assert.Equal(3m, first.Qty);
        Assert.Equal("BUC", first.Uom);
        Assert.Equal(50.58m, first.LineTotal);
    }

    // The printed line count (183, ending at VR023AC/1.08) must equal the number of
    // extracted lines, and the line sum must equal the invoice total (20882,55).
    [Fact]
    public void AllLinesExtracted_AndTotalsBalance()
    {
        var invoice = Run("fv_2624394.pdf");
        Assert.Equal(183, invoice.Lines.Count);
        Assert.Equal(183, invoice.ExpectedLineCount);
        Assert.Equal("VR023AC/1.08", invoice.Lines[^1].CodIntern);
        Assert.Equal(20882.55m, invoice.NetTotal);
        Assert.InRange(invoice.Lines.Sum(l => l.LineTotal), 20882.54m, 20882.56m);
    }

    // Rod profiles print an "L<length>" unit (L7, L6,5, L4,32 …) instead of LGT.
    // Every such L-length notation must normalize to BARE.
    [Fact]
    public void LengthNotationUnit_NormalizesToBare()
    {
        var invoice = Run("fv_2624394.pdf");

        Assert.Equal("BARE", invoice.Lines.First(l => l.CodIntern == "UG050").Uom);        // "L7"
        Assert.Equal("BARE", invoice.Lines.First(l => l.CodIntern == "UG852/6.5").Uom);    // "L6,5"
        Assert.Equal("BARE", invoice.Lines.First(l => l.CodIntern == "VR023AC/1.08").Uom); // "L1,08"

        // No extracted unit may remain in the raw "L<length>" form.
        Assert.DoesNotContain(invoice.Lines, l =>
            l.Uom != null && System.Text.RegularExpressions.Regex.IsMatch(l.Uom, @"^L\s*\d"));
    }

    // The line-count gate must not block valid invoices: the printed count must match
    // the extracted count on every current Aliplast fixture.
    [Theory]
    [InlineData("fv_2621671.pdf")]
    [InlineData("fv_2622984.pdf")]
    [InlineData("fv_2624394.pdf")]
    public void ExpectedLineCount_MatchesExtractedCount(string fixture)
    {
        var invoice = Run(fixture);
        Assert.NotNull(invoice.ExpectedLineCount);
        Assert.Equal(invoice.Lines.Count, invoice.ExpectedLineCount);
    }
}
