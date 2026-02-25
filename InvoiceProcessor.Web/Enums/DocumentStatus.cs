namespace InvoiceProcessor.Web.Enums;

public enum DocumentStatus
{
    Received,
    Extracting,
    Extracted,
    Classified,
    Parsed,
    Matched,
    Validated,
    ReadyToPost,
    Posting,
    Posted,
    NeedsReview,
    NeedsOcr,
    Failed,
    Duplicate
}
