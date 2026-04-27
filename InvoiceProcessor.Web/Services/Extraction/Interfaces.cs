using InvoiceProcessor.Web.Models;

namespace InvoiceProcessor.Web.Services.Extraction;

public interface IExtractionPipeline
{
    Task ProcessDocumentAsync(Guid documentId, CancellationToken cancellationToken);
    Task ProcessPendingAsync(CancellationToken cancellationToken);
    Task ReextractDocumentAsync(Guid documentId, CancellationToken cancellationToken);
}

public interface IDocumentClassifier
{
    (Enums.DocumentType type, decimal confidence, string? supplierName, string? vatNo) Classify(string text);
}

public interface ICanonicalParser
{
    string ParseInvoiceJson(string text, string strategy);
    string ParseMaterialsJson(string text, string strategy);
}
