namespace InvoiceProcessor.Web.Models;

public class Supplier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? ErpName { get; set; }
    public string? VatNo { get; set; }
    public string? Country { get; set; }
    public string AliasesJson { get; set; } = "[]";
    public bool Active { get; set; } = true;
    public ICollection<Document> Documents { get; set; } = [];

    /// <summary>Returns ErpName if set, otherwise falls back to Name.</summary>
    public string DisplayName => ErpName ?? Name;
}
