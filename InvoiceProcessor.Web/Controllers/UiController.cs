using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Services.Robot;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Controllers;

[ApiController]
[Route("ui")]
public class UiController(AppDbContext db, IPostingJobService postingJobService) : ControllerBase
{
    [HttpGet("suppliers")]
    public async Task<IActionResult> GetSuppliers(CancellationToken cancellationToken)
    {
        var docs = db.Documents.AsQueryable();
        var suppliers = await db.Suppliers
            .Select(s => new
            {
                s.Id,
                s.Name,
                Counts = docs.Where(d => d.SupplierId == s.Id)
                    .GroupBy(d => d.Status)
                    .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
            }).ToListAsync(cancellationToken);
        return Ok(suppliers);
    }

    [HttpGet("documents")]
    public async Task<IActionResult> GetDocuments([FromQuery] Guid? supplierId, [FromQuery] DocumentStatus? status, CancellationToken cancellationToken)
    {
        var query = db.Documents.Include(d => d.Supplier).AsQueryable();
        if (supplierId.HasValue) query = query.Where(d => d.SupplierId == supplierId);
        if (status.HasValue) query = query.Where(d => d.Status == status);
        var results = await query.OrderByDescending(d => d.CreatedAt)
            .Select(d => new
            {
                d.Id,
                d.Filename,
                d.DocType,
                d.InvoiceDate,
                d.InvoiceNo,
                d.GrossTotal,
                d.Status,
                Supplier = d.Supplier != null ? d.Supplier.Name : "Unknown"
            }).ToListAsync(cancellationToken);
        return Ok(results);
    }

    [HttpGet("documents/{id:guid}/pdf")]
    public async Task<IActionResult> GetPdf(Guid id, CancellationToken cancellationToken)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (doc is null || !System.IO.File.Exists(doc.StoragePath)) return NotFound();
        return PhysicalFile(Path.GetFullPath(doc.StoragePath), "application/pdf", enableRangeProcessing: true);
    }

    [HttpGet("documents/{id:guid}/canonical")]
    public async Task<IActionResult> GetCanonical(Guid id, CancellationToken cancellationToken)
    {
        var artifact = await db.ExtractArtifacts.FirstOrDefaultAsync(x => x.DocumentId == id, cancellationToken);
        if (artifact is null) return NotFound();
        return Content(artifact.CanonicalJson, "application/json");
    }

    [HttpPost("posting-jobs")]
    public async Task<IActionResult> CreatePostingJobs([FromBody] CreatePostingJobsRequest request, CancellationToken cancellationToken)
    {
        var jobs = await postingJobService.CreatePostingJobsAsync(request.DocumentIds, cancellationToken);
        return Ok(new { created = jobs.Count, jobs = jobs.Select(j => new { j.Id, j.DocumentId, j.Status, j.BatchId }) });
    }

    [HttpGet("posting-jobs")]
    public async Task<IActionResult> GetPostingJobs([FromQuery] PostingJobStatus? status, CancellationToken cancellationToken)
    {
        var query = db.PostingJobs.Include(j => j.Document).AsQueryable();
        if (status.HasValue) query = query.Where(j => j.Status == status);
        var jobs = await query.OrderByDescending(j => j.CreatedAt).Select(j => new
        {
            j.Id,
            j.DocumentId,
            j.BatchId,
            j.Status,
            j.ErpDocNo,
            j.ErrorCategory,
            j.ErrorMessage,
            SupplierId = j.Document.SupplierId,
            j.CreatedAt,
            j.CompletedAt
        }).ToListAsync(cancellationToken);
        return Ok(jobs);
    }
}
