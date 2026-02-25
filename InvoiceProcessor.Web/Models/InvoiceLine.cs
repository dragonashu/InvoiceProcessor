namespace InvoiceProcessor.Web.Models;

public class InvoiceLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = default!;
    public int LineNo { get; set; }
    public string? VendorCode { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public string? Uom { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public Guid? MatchedItemId { get; set; }
    public CatalogItem? MatchedItem { get; set; }
    public decimal MatchConfidence { get; set; }
    public string? MatchReason { get; set; }
}
