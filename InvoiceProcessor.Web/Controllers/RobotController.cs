using System.Text.Json;
using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using InvoiceProcessor.Web.Services.Robot;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Controllers;

[ApiController]
[Route("robot/jobs")]
public class RobotController(IPostingJobService postingJobService, AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        PostingJobStatus? parsed = null;
        if (status is not null && Enum.TryParse<PostingJobStatus>(status, true, out var s))
            parsed = s;

        var jobs = await postingJobService.ListJobsAsync(parsed, Math.Clamp(limit, 1, 200), cancellationToken);

        return Ok(jobs.Select(j =>
        {
            var payload = JsonSerializer.Deserialize<ReadyToPostInvoicePayload>(j.RequestJson);
            return new
            {
                j.Id,
                j.DocumentId,
                j.BatchId,
                Status = j.Status.ToString(),
                j.CreatedAt,
                j.ClaimedAt,
                j.CompletedAt,
                j.ErpDocNo,
                j.ErrorCategory,
                j.ErrorMessage,
                DocumentCorrelationId = j.Document?.CorrelationId,
                DocumentSupplier = j.Document?.Supplier?.DisplayName,
                Currency = payload?.Currency,
                TransactionType = payload?.TransactionType,
                WarehouseCode = payload?.WarehouseCode,
                CustomsMrn = payload?.CustomsMrn,
                CustomsLrn = payload?.CustomsLrn,
                CustomsExchangeRate = payload?.CustomsExchangeRate,
                CustomsReleaseDate = payload?.CustomsReleaseDate
            };
        }));
    }

    [HttpGet("next")]
    public async Task<IActionResult> Next(CancellationToken cancellationToken)
    {
        var payload = await postingJobService.ClaimNextJobAsync(cancellationToken);
        return payload is null ? NoContent() : Ok(payload);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var job = await postingJobService.GetJobAsync(id, cancellationToken);
        if (job is null) return NotFound();

        var payload = JsonSerializer.Deserialize<ReadyToPostInvoicePayload>(job.RequestJson);
        return Ok(new
        {
            job.Id,
            job.DocumentId,
            job.BatchId,
            Status = job.Status.ToString(),
            job.CreatedAt,
            job.ClaimedAt,
            job.CompletedAt,
            job.ErpDocNo,
            job.ErrorCategory,
            job.ErrorMessage,
            Currency = payload?.Currency,
            TransactionType = payload?.TransactionType,
            WarehouseCode = payload?.WarehouseCode,
            CustomsMrn = payload?.CustomsMrn,
            CustomsLrn = payload?.CustomsLrn,
            CustomsExchangeRate = payload?.CustomsExchangeRate,
            CustomsReleaseDate = payload?.CustomsReleaseDate,
            job.RequestJson,
            job.ResultJson
        });
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] RobotUpdateRequest request, CancellationToken cancellationToken)
    {
        var job = await postingJobService.UpdateJobAsync(id, request, cancellationToken);
        return Ok(new { job.Id, Status = job.Status.ToString() });
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, [FromBody] RobotCompleteRequest request, CancellationToken cancellationToken)
    {
        await postingJobService.CompleteJobAsync(id, request, cancellationToken);
        return Ok(new { id, status = request.Result });
    }

    // ─── Catalog item jobs (same pattern: queue → claim → complete) ───

    [HttpGet("catalog")]
    public async Task<IActionResult> ListCatalogJobs([FromQuery] string? status, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        var query = db.CatalogJobs.Include(j => j.CatalogItem).AsQueryable();
        if (status is not null && Enum.TryParse<CatalogJobStatus>(status, true, out var s))
            query = query.Where(j => j.Status == s);
        var jobs = await query.OrderBy(j => j.CreatedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
        return Ok(jobs.Select(j =>
        {
            var p = JsonSerializer.Deserialize<CatalogItemPayload>(j.RequestJson);
            return new
            {
                j.Id, j.CatalogItemId, j.BatchId, Status = j.Status.ToString(),
                j.CreatedAt, j.ClaimedAt, j.CompletedAt, j.ErpItemCode, j.ErrorMessage,
                ItemCode = j.CatalogItem?.ErpItemCode, ItemName = j.CatalogItem?.Name, ItemUom = j.CatalogItem?.Uom,
                p?.ExternalCode, p?.PropertyClass
            };
        }));
    }

    [HttpGet("catalog/next")]
    public async Task<IActionResult> NextCatalogJob(CancellationToken cancellationToken)
    {
        var job = await db.CatalogJobs
            .Include(j => j.CatalogItem)
            .Where(j => j.Status == CatalogJobStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (job is null) return NoContent();

        job.Status = CatalogJobStatus.Claimed;
        job.ClaimedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var payload = JsonSerializer.Deserialize<CatalogItemPayload>(job.RequestJson);
        return Ok(payload);
    }

    [HttpPost("catalog/{id:guid}/complete")]
    public async Task<IActionResult> CompleteCatalogJob(Guid id, [FromBody] CatalogJobCompleteRequest request, CancellationToken cancellationToken)
    {
        var job = await db.CatalogJobs.Include(j => j.CatalogItem).FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (job is null) return NotFound();

        job.CompletedAt = DateTime.UtcNow;
        job.ResultJson = request.ResultJson;
        job.ErrorMessage = request.ErrorMessage;

        if (request.Result.Equals("Success", StringComparison.OrdinalIgnoreCase))
        {
            job.Status = CatalogJobStatus.Success;
            // Update the catalog item with the confirmed code if the robot assigned one
            if (!string.IsNullOrWhiteSpace(request.InternalCode) && job.CatalogItem is not null)
            {
                job.CatalogItem.ErpItemCode = request.InternalCode;
                job.ErpItemCode = request.InternalCode;
            }
            // Mark as no longer auto-created — it's now in ERP
            if (job.CatalogItem is not null)
                job.CatalogItem.IsAutoCreated = false;
        }
        else
        {
            job.Status = CatalogJobStatus.Failed;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { id, status = job.Status.ToString() });
    }
}
