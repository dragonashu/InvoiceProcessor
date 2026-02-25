namespace InvoiceProcessor.Web.Models;

public class ExtractArtifact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = default!;
    public string ExtractedJson { get; set; } = "{}";
    public string CanonicalJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
