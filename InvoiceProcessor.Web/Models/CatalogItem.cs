namespace InvoiceProcessor.Web.Models;

public class CatalogItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ErpItemCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Uom { get; set; }
    public string? TaxCode { get; set; }
    public bool Active { get; set; } = true;
    public bool IsAutoCreated { get; set; }
    public DateTime? AutoCreatedAt { get; set; }
}
