namespace InvoiceProcessor.Web.Enums;

public enum PostingJobStatus
{
    Queued,
    Claimed,
    Running,
    Success,
    Failed,
    Partial
}
