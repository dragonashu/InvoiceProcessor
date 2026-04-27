namespace InvoiceProcessor.Web.Models;

public class CustomsDeclaration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Filename { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public string? Mrn { get; set; }
    public string? Lrn { get; set; }
    public decimal? ExchangeRate { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public string? InvoiceRef { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
