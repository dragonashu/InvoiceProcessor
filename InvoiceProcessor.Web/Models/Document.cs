using InvoiceProcessor.Web.Enums;

namespace InvoiceProcessor.Web.Models;

public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "EMAIL";
    public string? EmailFrom { get; set; }
    public string? EmailSubject { get; set; }
    public string Filename { get; set; } = string.Empty;
    public string PdfHash { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public DocumentType DocType { get; set; } = DocumentType.Unknown;
    public Guid? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Received;
    public decimal Confidence { get; set; }
    public string? InvoiceNo { get; set; }
    public DateOnly? InvoiceDate { get; set; }
    public decimal? GrossTotal { get; set; }
    public bool IsImport { get; set; }
    public string? WarehouseCode { get; set; }
    public Guid? CustomsDeclarationId { get; set; }
    public CustomsDeclaration? CustomsDeclaration { get; set; }
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public ExtractArtifact? ExtractArtifact { get; set; }
    public ICollection<InvoiceLine> InvoiceLines { get; set; } = [];
    public ICollection<PostingJob> PostingJobs { get; set; } = [];
}
