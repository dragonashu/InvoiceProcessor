namespace InvoiceProcessor.Web.Models;

public class CatalogImportLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    public CatalogImportSource Source { get; set; }
    public int AddedCount { get; set; }
    public int UpdatedCount { get; set; }
    public string? FileName { get; set; }
}

public enum CatalogImportSource
{
    Manual,
    Api
}
