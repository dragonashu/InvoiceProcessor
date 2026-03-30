namespace InvoiceProcessor.Web.Services.Matching;

public interface IMatchingEngine
{
    Task MatchInvoiceLinesAsync(Guid documentId, string canonicalJson, CancellationToken cancellationToken);
    Task<(int added, int updated)> ImportCatalogCsvAsync(Stream stream, CancellationToken cancellationToken);
}
