namespace InvoiceProcessor.Web.Models;

public class Supplier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? VatNo { get; set; }
    public string? Country { get; set; }
    public string AliasesJson { get; set; } = "[]";
    public bool Active { get; set; } = true;
    public ICollection<Document> Documents { get; set; } = [];
}
