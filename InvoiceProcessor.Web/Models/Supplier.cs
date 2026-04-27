using InvoiceProcessor.Web.Enums;

namespace InvoiceProcessor.Web.Models;

public class Supplier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? ErpName { get; set; }
    public string? VatNo { get; set; }
    public string? Country { get; set; }
    public string AliasesJson { get; set; } = "[]";
    public InvoiceType InvoiceType { get; set; } = InvoiceType.Intern;
    public TaxationType TaxationType { get; set; } = TaxationType.TaxareNormala;
    public TransactionType TransactionType { get; set; } = TransactionType.TranzactieInterna;
    public bool Active { get; set; } = true;
    public ICollection<Document> Documents { get; set; } = [];

    /// <summary>Returns ErpName if set, otherwise falls back to Name.</summary>
    public string DisplayName => ErpName ?? Name;
    public bool IsImportSupplier => InvoiceType == InvoiceType.Import;
}
