using System.Text.RegularExpressions;
using InvoiceProcessor.Web.Enums;

namespace InvoiceProcessor.Web.Services.Extraction;

public class RuleBasedDocumentClassifier : IDocumentClassifier
{
    private static readonly string[] InvoiceKeywords =
    [
        // English
        "invoice", "vat", "net total", "gross total", "tax invoice",
        // Romanian
        "factura", "factură", "tva", "baza de impozita", "valoare netă",
        "total factură", "total factura", "nr. factură", "nr. factura",
        // German
        "rechnung", "mehrwertsteuer", "nettobetrag",
        // Slovak/Czech
        "faktúra", "faktura", "dph",
        // Spanish
        "factura", "iva", "importe total",
        // Polish
        "faktura", "netto", "brutto",
    ];

    private static readonly string[] MaterialsKeywords =
    [
        // English
        "materials", "consumed", "job reference", "materials list",
        // Romanian
        "materiale", "consumate", "referință", "lista de materiale",
        // Spanish
        "materiales", "consumidos", "referencia de trabajo",
    ];

    // EU VAT number pattern: 2-letter country code followed by 8-12 alphanumeric characters
    private static readonly Regex VatPattern = new(@"\b([A-Z]{2}\d{8,12})\b", RegexOptions.Compiled);

    // "Supplier:" label pattern (English invoices)
    private static readonly Regex SupplierLabelPattern = new(@"Supplier[:\s]+(?<name>.+)", RegexOptions.Compiled);

    public (DocumentType type, decimal confidence, string? supplierName, string? vatNo) Classify(string text)
    {
        var normalized = text.ToLowerInvariant();

        var invoiceScore = InvoiceKeywords.Count(kw => normalized.Contains(kw));
        var materialsScore = MaterialsKeywords.Count(kw => normalized.Contains(kw));

        DocumentType type;
        decimal confidence;

        if (invoiceScore > 0 && invoiceScore >= materialsScore)
        {
            type = DocumentType.Invoice;
            // Scale confidence based on how many keywords matched
            confidence = Math.Min(0.95m, 0.60m + invoiceScore * 0.05m);
        }
        else if (materialsScore > 0)
        {
            type = DocumentType.MaterialsList;
            confidence = Math.Min(0.95m, 0.60m + materialsScore * 0.05m);
        }
        else
        {
            type = DocumentType.Unknown;
            confidence = 0.20m;
        }

        // Extract VAT numbers — the first match is typically the supplier (appears in the header)
        var vatMatches = VatPattern.Matches(text);
        string? supplierVat = vatMatches.Count > 0 ? vatMatches[0].Value : null;

        // Try to extract supplier name from explicit label or from text near the VAT
        var supplierMatch = SupplierLabelPattern.Match(text);
        string? supplierName = supplierMatch.Success ? supplierMatch.Groups["name"].Value.Trim() : null;

        return (type, confidence, supplierName, supplierVat);
    }
}
