namespace InvoiceProcessor.Web.Contracts;

public record CanonicalInvoice(
    string? Supplier,
    string? InvoiceNo,
    DateOnly? InvoiceDate,
    string? Currency,
    decimal? NetTotal,
    decimal? VatTotal,
    decimal? GrossTotal,
    IReadOnlyList<CanonicalInvoiceLine> Lines,
    CanonicalMetadata Metadata);

public record CanonicalInvoiceLine(string? CodIntern, string DescriptionRaw, decimal Qty, string? Uom, decimal? UnitPrice, decimal LineTotal, string? Bare = null, string? ExternalCode = null, string? PropertyClass = null);

public record CanonicalMaterialsList(
    string? JobReference,
    IReadOnlyList<CanonicalMaterialLine> Lines,
    CanonicalMetadata Metadata);

public record CanonicalMaterialLine(string Description, decimal Qty, string? Uom, string? Code);

public record CanonicalMetadata(decimal Confidence, string Strategy, string? Notes = null);

public record ReadyToPostInvoicePayload(Guid PostingJobId, Guid DocumentId, string CorrelationId, string? SupplierErpName, bool IsImport, string InvoiceType, string TaxationType, string TransactionType, string? InvoiceNo, DateOnly? InvoiceDate, string? Currency, decimal? GrossTotal, string? WarehouseCode, string? CustomsMrn, string? CustomsLrn, decimal? CustomsExchangeRate, DateOnly? CustomsReleaseDate, IReadOnlyList<ReadyToPostLine> Lines);
public record ReadyToPostLine(int LineNo, string Description, decimal Qty, string? Uom, decimal Amount, string? ErpItemCode, string? ErpItemName, decimal Confidence, string Reason, string? WarehouseCode = null, string? CostCenterCode = null, string? ExternalCode = null, string? PropertyClass = null);

public record RobotCompleteRequest(string Result, string? ErpDocNo, string? ErrorCategory, string? ErrorMessage, string? ResultJson);

public record RobotUpdateRequest(string? Status, string? ErpDocNo, string? ErrorCategory, string? ErrorMessage, string? ResultJson);

public record CreatePostingJobsRequest(IReadOnlyList<Guid> DocumentIds);

public record SupplierRequest(string Name, string? ErpName, string? VatNo, string? Country, string? AliasesJson, bool Active = true);

public record SaveMappingRequest(Guid SupplierId, string VendorCode, Guid CatalogItemId, Guid? InvoiceLineId);

public record AcceptNewItemRequest(string ErpItemCode, string Name, string? Uom, string? ExternalCode = null, string? PropertyClass = null);

public record CatalogItemPayload(Guid CatalogJobId, Guid CatalogItemId, string Code, string Name, string? Uom, string? ExternalCode = null, string? PropertyClass = null);

public record CatalogJobCompleteRequest(string Result, string? InternalCode, string? ErrorMessage, string? ResultJson);
