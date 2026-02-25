using System.Text.Json;
using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Services.Robot;

public class PostingJobService(AppDbContext db, IOrchestratorClient orchestratorClient) : IPostingJobService
{
    public async Task<IReadOnlyList<PostingJob>> CreatePostingJobsAsync(IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken)
    {
        var docs = await db.Documents.Include(d => d.InvoiceLines).Include(d => d.ExtractArtifact).Where(d => documentIds.Contains(d.Id)).ToListAsync(cancellationToken);
        var batchId = Guid.NewGuid().ToString("N");
        var jobs = new List<PostingJob>();

        foreach (var doc in docs)
        {
            if (doc.Status is not (DocumentStatus.ReadyToPost or DocumentStatus.NeedsReview)) continue;

            var canonical = JsonSerializer.Deserialize<CanonicalInvoice>(doc.ExtractArtifact?.CanonicalJson ?? "{}");
            if (canonical is null) continue;

            var payload = new ReadyToPostInvoicePayload(
                Guid.NewGuid(),
                doc.Id,
                doc.CorrelationId,
                canonical,
                doc.InvoiceLines.OrderBy(l => l.LineNo).Select(l => new ReadyToPostLine(l.LineNo, l.Description, l.Qty, l.Uom, l.Amount, l.MatchedItem?.ErpItemCode, l.MatchConfidence, l.MatchReason ?? string.Empty)).ToList());

            var job = new PostingJob
            {
                Id = payload.PostingJobId,
                DocumentId = doc.Id,
                BatchId = batchId,
                Status = PostingJobStatus.Queued,
                RequestJson = JsonSerializer.Serialize(payload)
            };

            jobs.Add(job);
            doc.Status = DocumentStatus.Posting;
            db.PostingJobs.Add(job);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (jobs.Count > 0)
        {
            await orchestratorClient.TriggerProcessAsync(batchId, cancellationToken);
        }
        return jobs;
    }

    public async Task<ReadyToPostInvoicePayload?> ClaimNextJobAsync(CancellationToken cancellationToken)
    {
        var job = await db.PostingJobs
            .Where(j => j.Status == PostingJobStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (job is null) return null;

        job.Status = PostingJobStatus.Claimed;
        job.ClaimedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return JsonSerializer.Deserialize<ReadyToPostInvoicePayload>(job.RequestJson);
    }

    public async Task CompleteJobAsync(Guid jobId, RobotCompleteRequest request, CancellationToken cancellationToken)
    {
        var job = await db.PostingJobs.Include(j => j.Document).FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
                  ?? throw new InvalidOperationException("Job not found");

        job.CompletedAt = DateTimeOffset.UtcNow;
        job.ResultJson = request.ResultJson;
        job.ErpDocNo = request.ErpDocNo;
        job.ErrorCategory = request.ErrorCategory;
        job.ErrorMessage = request.ErrorMessage;

        job.Status = request.Result.ToUpperInvariant() switch
        {
            "SUCCESS" => PostingJobStatus.Success,
            "PARTIAL" => PostingJobStatus.Partial,
            _ => PostingJobStatus.Failed
        };

        job.Document.Status = job.Status switch
        {
            PostingJobStatus.Success => DocumentStatus.Posted,
            PostingJobStatus.Partial => DocumentStatus.NeedsReview,
            _ => DocumentStatus.Failed
        };

        db.AuditEvents.Add(new AuditEvent
        {
            DocumentId = job.DocumentId,
            EventType = "ROBOT_COMPLETE",
            Message = $"Job {job.Status}",
            PayloadJson = JsonSerializer.Serialize(request)
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
