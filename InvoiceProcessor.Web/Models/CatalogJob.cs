namespace InvoiceProcessor.Web.Models;

public class CatalogJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CatalogItemId { get; set; }
    public CatalogItem CatalogItem { get; set; } = default!;
    public string BatchId { get; set; } = string.Empty;
    public CatalogJobStatus Status { get; set; } = CatalogJobStatus.Queued;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClaimedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string RequestJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public string? ErpItemCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public enum CatalogJobStatus
{
    Queued,
    Claimed,
    Running,
    Success,
    Failed
}
