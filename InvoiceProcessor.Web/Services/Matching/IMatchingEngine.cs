using InvoiceProcessor.Web.Models;

namespace InvoiceProcessor.Web.Services.Matching;

public interface IMatchingEngine
{
    Task MatchInvoiceLinesAsync(Guid documentId, string canonicalJson, CancellationToken cancellationToken);
    Task<(int added, int updated)> ImportCatalogXlsxAsync(Stream stream, CatalogImportSource source, string? fileName, CancellationToken cancellationToken);
}
