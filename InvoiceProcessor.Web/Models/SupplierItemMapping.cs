namespace InvoiceProcessor.Web.Models;

public class SupplierItemMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = default!;
    public string? VendorCode { get; set; }
    public string? Pattern { get; set; }
    public Guid CatalogItemId { get; set; }
    public CatalogItem CatalogItem { get; set; } = default!;
    public bool Active { get; set; } = true;
}
