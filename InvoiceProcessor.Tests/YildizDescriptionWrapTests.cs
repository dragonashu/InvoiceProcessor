using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Services.Extraction;
using UglyToad.PdfPig;
using Xunit;

namespace InvoiceProcessor.Tests;

public class YildizDescriptionWrapTests
{
    private static CanonicalInvoice Run(string name)
    {
        var pdfPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../samples/", name));
        Assert.True(File.Exists(pdfPath), $"Fixture not found: {pdfPath}");
        using var pdf = PdfDocument.Open(pdfPath);
        var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        return new YildizInvoiceExtractor().Extract(pdf, text);
    }

    [Fact]
    public void UvTecPaulestiDescription_FullWrappedText_IsCaptured()
    {
        var invoice = Run("17.03.2026-INV.pdf");
        const string expected =
            "6 Cool Lite KN 166 II ESG / 12 WE Negru; Ar 90%, polyurethane / " +
            "6 mm clear / 12 WE Negru; Ar 90%, polyurethane / " +
            "6 mm sisecam LowE 71/53 #5, ESG";
        var match = invoice.Lines.FirstOrDefault(l => l.DescriptionRaw.Contains("Cool Lite"));
        Assert.NotNull(match);
        Assert.Equal(expected, match!.DescriptionRaw);
    }

    [Fact]
    public void AprilInvoice_GrandTotalMatchesPrintedFooter()
    {
        // Printed footer: subtotal 22,155.83 − discount 676.00 = grand total 21,479.83 €.
        var invoice = Run("17.04.2026 INV.pdf");
        Assert.Equal(21479.83m, invoice.GrossTotal!.Value);
        Assert.InRange(invoice.Lines.Sum(l => l.LineTotal), 22155.82m, 22155.84m);
    }

    [Fact]
    public void AprilInvoice_DuplicateSourceRow_IsPreserved()
    {
        // 3660404-26 (Code:304, 1500×930, 6 pcs, 378.00) is printed twice on page 2.
        // Preserving it keeps the footer control totals (454 pcs / 577.87 m²) intact.
        var invoice = Run("17.04.2026 INV.pdf");
        var delia = invoice.Lines.Single(l => l.DescriptionRaw.Contains("88.2 tempered"));
        Assert.Equal(395.98m, delia.Qty);
        Assert.Equal(17819.10m, delia.LineTotal);
    }

    [Fact]
    public void ProjectNormalization_HyphenAndTurkishI_ConsolidateIntoOneGroup()
    {
        // March has both "PHASE 1" and "PHASE-1"; April has Turkish "VİVALDI PROJECT PHASE 2".
        // Same description at the same unit price must collapse into a single group.
        var march = Run("17.03.2026-INV.pdf");
        Assert.Single(march.Lines, l => l.DescriptionRaw.StartsWith("4 mm clear glass"));
        Assert.Single(march.Lines, l => l.DescriptionRaw.StartsWith("4 mm satinated"));

        var april = Run("17.04.2026 INV.pdf");
        Assert.Single(april.Lines, l => l.DescriptionRaw.StartsWith("4 mm clear glass"));
        Assert.Single(april.Lines, l => l.DescriptionRaw.StartsWith("4 mm satinated"));
    }
}
