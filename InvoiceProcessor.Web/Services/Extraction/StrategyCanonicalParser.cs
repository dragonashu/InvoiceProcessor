using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceProcessor.Web.Contracts;

namespace InvoiceProcessor.Web.Services.Extraction;

public class StrategyCanonicalParser : ICanonicalParser
{
    public string ParseInvoiceJson(string text, string strategy)
    {
        var invoiceNo = Regex.Match(text, @"Invoice\s*(No|#)?\s*[:\-]?\s*(?<no>[A-Za-z0-9\-/]+)", RegexOptions.IgnoreCase).Groups["no"].Value;
        var gross = TryParseDecimal(Regex.Match(text, @"(Gross|Total)\s*[:]?\s*(?<val>[0-9.,]+)", RegexOptions.IgnoreCase).Groups["val"].Value);

        var model = new CanonicalInvoice(
            Supplier: null,
            InvoiceNo: string.IsNullOrWhiteSpace(invoiceNo) ? null : invoiceNo,
            InvoiceDate: DateOnly.FromDateTime(DateTime.UtcNow),
            Currency: "PLN",
            NetTotal: gross,
            VatTotal: null,
            GrossTotal: gross,
            Lines: [new CanonicalInvoiceLine(null, "Generic parsed line", 1, "szt", gross, gross ?? 0)],
            Metadata: new CanonicalMetadata(0.70m, strategy));

        return JsonSerializer.Serialize(model);
    }

    public string ParseMaterialsJson(string text, string strategy)
    {
        var model = new CanonicalMaterialsList(
            JobReference: Regex.Match(text, @"Job\s*Reference[:\s]+(?<r>[A-Za-z0-9\-/]+)", RegexOptions.IgnoreCase).Groups["r"].Value,
            Lines: [new CanonicalMaterialLine("Material line", 1, "szt", null)],
            Metadata: new CanonicalMetadata(0.65m, strategy));
        return JsonSerializer.Serialize(model);
    }

    private static decimal? TryParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace(" ", string.Empty).Replace(",", ".");
        return decimal.TryParse(normalized, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
