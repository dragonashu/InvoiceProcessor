using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;

namespace InvoiceProcessor.Web.Services.Robot;

public interface IPostingJobService
{
    Task<IReadOnlyList<PostingJob>> CreatePostingJobsAsync(IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<PostingJob>> ListJobsAsync(PostingJobStatus? status, int limit, CancellationToken cancellationToken);
    Task<PostingJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken);
    Task<ReadyToPostInvoicePayload?> ClaimNextJobAsync(CancellationToken cancellationToken);
    Task<PostingJob> UpdateJobAsync(Guid jobId, RobotUpdateRequest request, CancellationToken cancellationToken);
    Task CompleteJobAsync(Guid jobId, RobotCompleteRequest request, CancellationToken cancellationToken);
}
