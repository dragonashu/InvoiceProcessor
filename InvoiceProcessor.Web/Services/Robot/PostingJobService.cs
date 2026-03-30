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
        var docs = await db.Documents.Include(d => d.InvoiceLines).ThenInclude(l => l.MatchedItem).Include(d => d.ExtractArtifact).Where(d => documentIds.Contains(d.Id)).ToListAsync(cancellationToken);
        var batchId = Guid.NewGuid().ToString("N");
        var jobs = new List<PostingJob>();

        foreach (var doc in docs)
        {
            if (doc.Status is not DocumentStatus.ReadyToPost) continue;

            var canonical = JsonSerializer.Deserialize<CanonicalInvoice>(doc.ExtractArtifact?.CanonicalJson ?? "{}");
            if (canonical is null) continue;

            var payload = new ReadyToPostInvoicePayload(
                Guid.NewGuid(),
                doc.Id,
                doc.CorrelationId,
                canonical,
                doc.InvoiceLines.OrderBy(l => l.LineNo).Select(l => new ReadyToPostLine(l.LineNo, l.Description, l.Qty, l.Uom, l.Amount, l.MatchedItem?.ErpItemCode, l.MatchedItem?.Name, l.MatchConfidence, l.MatchReason ?? string.Empty, l.WarehouseCode, l.CostCenterCode)).ToList());

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

            // Learn: save vendor code -> catalog item mappings for matched lines
            if (doc.SupplierId.HasValue)
                await LearnMappingsAsync(doc.SupplierId.Value, doc.InvoiceLines, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (jobs.Count > 0)
        {
            await orchestratorClient.TriggerProcessAsync(batchId, cancellationToken);
        }
        return jobs;
    }

    public async Task<IReadOnlyList<PostingJob>> ListJobsAsync(PostingJobStatus? status, int limit, CancellationToken cancellationToken)
    {
        var query = db.PostingJobs.Include(j => j.Document).AsQueryable();
        if (status.HasValue)
            query = query.Where(j => j.Status == status.Value);
        return await query.OrderBy(j => j.CreatedAt).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<PostingJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        return await db.PostingJobs.Include(j => j.Document).FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
    }

    public async Task<ReadyToPostInvoicePayload?> ClaimNextJobAsync(CancellationToken cancellationToken)
    {
        var job = await db.PostingJobs
            .Where(j => j.Status == PostingJobStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (job is null) return null;

        job.Status = PostingJobStatus.Claimed;
        job.ClaimedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return JsonSerializer.Deserialize<ReadyToPostInvoicePayload>(job.RequestJson);
    }

    public async Task<PostingJob> UpdateJobAsync(Guid jobId, RobotUpdateRequest request, CancellationToken cancellationToken)
    {
        var job = await db.PostingJobs.Include(j => j.Document).FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
                  ?? throw new InvalidOperationException("Job not found");

        if (request.Status is not null && Enum.TryParse<PostingJobStatus>(request.Status, true, out var newStatus))
        {
            job.Status = newStatus;
            if (newStatus == PostingJobStatus.Running && job.ClaimedAt is null)
                job.ClaimedAt = DateTime.UtcNow;
        }

        if (request.ErpDocNo is not null) job.ErpDocNo = request.ErpDocNo;
        if (request.ErrorCategory is not null) job.ErrorCategory = request.ErrorCategory;
        if (request.ErrorMessage is not null) job.ErrorMessage = request.ErrorMessage;
        if (request.ResultJson is not null) job.ResultJson = request.ResultJson;

        db.AuditEvents.Add(new AuditEvent
        {
            DocumentId = job.DocumentId,
            EventType = "ROBOT_UPDATE",
            Message = $"Job updated: {job.Status}",
            PayloadJson = JsonSerializer.Serialize(request)
        });

        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task CompleteJobAsync(Guid jobId, RobotCompleteRequest request, CancellationToken cancellationToken)
    {
        var job = await db.PostingJobs.Include(j => j.Document).FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
                  ?? throw new InvalidOperationException("Job not found");

        job.CompletedAt = DateTime.UtcNow;
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

        // Learn on success: confirm all mappings from this document
        if (job.Status == PostingJobStatus.Success && job.Document.SupplierId.HasValue)
        {
            var lines = await db.InvoiceLines
                .Include(l => l.MatchedItem)
                .Where(l => l.DocumentId == job.DocumentId)
                .ToListAsync(cancellationToken);
            await LearnMappingsAsync(job.Document.SupplierId.Value, lines, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task LearnMappingsAsync(Guid supplierId, IEnumerable<InvoiceLine> lines, CancellationToken cancellationToken)
    {
        var existingMappings = await db.SupplierItemMappings
            .Where(m => m.SupplierId == supplierId && m.Active)
            .Select(m => m.VendorCode)
            .ToListAsync(cancellationToken);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.VendorCode) || line.MatchedItemId is null)
                continue;

            if (existingMappings.Contains(line.VendorCode))
                continue;

            db.SupplierItemMappings.Add(new SupplierItemMapping
            {
                SupplierId = supplierId,
                VendorCode = line.VendorCode,
                CatalogItemId = line.MatchedItemId.Value
            });
            existingMappings.Add(line.VendorCode);
        }
    }
}
