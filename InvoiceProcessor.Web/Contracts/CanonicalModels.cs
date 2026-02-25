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

public record CanonicalInvoiceLine(string? VendorItemCode, string DescriptionRaw, decimal Qty, string? Uom, decimal? UnitPrice, decimal LineTotal);

public record CanonicalMaterialsList(
    string? JobReference,
    IReadOnlyList<CanonicalMaterialLine> Lines,
    CanonicalMetadata Metadata);

public record CanonicalMaterialLine(string Description, decimal Qty, string? Uom, string? Code);

public record CanonicalMetadata(decimal Confidence, string Strategy, string? Notes = null);

public record ReadyToPostInvoicePayload(Guid PostingJobId, Guid DocumentId, string CorrelationId, CanonicalInvoice Invoice, IReadOnlyList<ReadyToPostLine> Lines);
public record ReadyToPostLine(int LineNo, string Description, decimal Qty, string? Uom, decimal Amount, string? ErpItemCode, decimal Confidence, string Reason);

public record RobotCompleteRequest(string Result, string? ErpDocNo, string? ErrorCategory, string? ErrorMessage, string? ResultJson);

public record CreatePostingJobsRequest(IReadOnlyList<Guid> DocumentIds);
