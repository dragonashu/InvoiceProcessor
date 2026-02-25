using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Models;

namespace InvoiceProcessor.Web.Services.Robot;

public interface IPostingJobService
{
    Task<IReadOnlyList<PostingJob>> CreatePostingJobsAsync(IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken);
    Task<ReadyToPostInvoicePayload?> ClaimNextJobAsync(CancellationToken cancellationToken);
    Task CompleteJobAsync(Guid jobId, RobotCompleteRequest request, CancellationToken cancellationToken);
}
