using InvoiceProcessor.Web.Enums;

namespace InvoiceProcessor.Web.Models;

public class PostingJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = default!;
    public string BatchId { get; set; } = string.Empty;
    public PostingJobStatus Status { get; set; } = PostingJobStatus.Queued;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClaimedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string RequestJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public string? ErpDocNo { get; set; }
    public string? ErrorCategory { get; set; }
    public string? ErrorMessage { get; set; }
}
