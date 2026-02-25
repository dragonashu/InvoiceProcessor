using System.Text.RegularExpressions;
using InvoiceProcessor.Web.Enums;

namespace InvoiceProcessor.Web.Services.Extraction;

public class RuleBasedDocumentClassifier : IDocumentClassifier
{
    public (DocumentType type, decimal confidence, string? supplierName, string? vatNo) Classify(string text)
    {
        var normalized = text.ToLowerInvariant();
        var isInvoice = normalized.Contains("invoice") || normalized.Contains("vat") || normalized.Contains("net total");
        var isMaterials = normalized.Contains("materials") || normalized.Contains("consumed") || normalized.Contains("job reference");

        var type = isInvoice ? DocumentType.Invoice : isMaterials ? DocumentType.MaterialsList : DocumentType.Unknown;
        var confidence = type == DocumentType.Unknown ? 0.2m : 0.82m;

        var vatMatch = Regex.Match(text, @"\b([A-Z]{2}[0-9A-Z]{8,12})\b");
        var supplierMatch = Regex.Match(text, @"Supplier[:\s]+(?<name>.+)");
        return (type, confidence, supplierMatch.Groups["name"].Value.Trim(), vatMatch.Success ? vatMatch.Value : null);
    }
}
