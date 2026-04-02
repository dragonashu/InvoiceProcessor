using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
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
                s.ErpName,
                DisplayName = s.ErpName ?? s.Name,
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
                Supplier = d.Supplier != null ? (d.Supplier.ErpName ?? d.Supplier.Name) : "Unknown"
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

    [HttpPost("suppliers")]
    public async Task<IActionResult> CreateSupplier([FromBody] SupplierRequest request, CancellationToken cancellationToken)
    {
        var supplier = new Supplier
        {
            Name = request.Name,
            ErpName = request.ErpName,
            VatNo = request.VatNo,
            Country = request.Country,
            AliasesJson = request.AliasesJson ?? "[]"
        };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { supplier.Id, supplier.Name, supplier.ErpName });
    }

    [HttpPut("suppliers/{id:guid}")]
    public async Task<IActionResult> UpdateSupplier(Guid id, [FromBody] SupplierRequest request, CancellationToken cancellationToken)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (supplier is null) return NotFound();

        supplier.Name = request.Name;
        supplier.ErpName = request.ErpName;
        supplier.VatNo = request.VatNo;
        supplier.Country = request.Country;
        supplier.AliasesJson = request.AliasesJson ?? "[]";
        supplier.Active = request.Active;

        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { supplier.Id, supplier.Name, supplier.ErpName });
    }

    [HttpDelete("suppliers/{id:guid}")]
    public async Task<IActionResult> DeleteSupplier(Guid id, CancellationToken cancellationToken)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (supplier is null) return NotFound();

        supplier.Active = false;
        await db.SaveChangesAsync(cancellationToken);
        return Ok();
    }

    [HttpGet("catalog/search")]
    public async Task<IActionResult> SearchCatalog([FromQuery] string q, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2) return Ok(Array.Empty<object>());

        var terms = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var items = await db.CatalogItems.Where(c => c.Active).ToListAsync(cancellationToken);

        var results = items
            .Select(c => new
            {
                c.Id,
                c.ErpItemCode,
                c.Name,
                c.Uom,
                Score = terms.Count(t => c.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || c.ErpItemCode.Contains(t, StringComparison.OrdinalIgnoreCase))
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name)
            .Take(15)
            .ToList();

        return Ok(results);
    }

    [HttpPost("catalog/map")]
    public async Task<IActionResult> SaveMapping([FromBody] SaveMappingRequest request, CancellationToken cancellationToken)
    {
        // Check if mapping already exists
        var existing = await db.SupplierItemMappings
            .FirstOrDefaultAsync(m => m.SupplierId == request.SupplierId && m.VendorCode == request.VendorCode && m.Active, cancellationToken);

        if (existing is not null)
        {
            existing.CatalogItemId = request.CatalogItemId;
        }
        else
        {
            db.SupplierItemMappings.Add(new Models.SupplierItemMapping
            {
                SupplierId = request.SupplierId,
                VendorCode = request.VendorCode,
                CatalogItemId = request.CatalogItemId
            });
        }

        // Also update the InvoiceLine if provided
        if (request.InvoiceLineId.HasValue)
        {
            var line = await db.InvoiceLines.FirstOrDefaultAsync(l => l.Id == request.InvoiceLineId, cancellationToken);
            if (line is not null)
            {
                line.MatchedItemId = request.CatalogItemId;
                line.MatchConfidence = 1.0m;
                line.MatchReason = "manual-match";
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return Ok();
    }

    [HttpGet("catalog/new-items")]
    public async Task<IActionResult> GetNewItems(CancellationToken cancellationToken)
    {
        var items = await db.CatalogItems
            .Where(c => c.IsAutoCreated && c.Active)
            .OrderByDescending(c => c.AutoCreatedAt)
            .Select(c => new { c.Id, c.ErpItemCode, c.Name, c.Uom, c.AutoCreatedAt })
            .ToListAsync(cancellationToken);

        // Find source document + line number for each new item
        var itemIds = items.Select(i => i.Id).ToList();
        var sources = await db.InvoiceLines
            .Where(l => l.MatchedItemId != null && itemIds.Contains(l.MatchedItemId.Value))
            .Include(l => l.Document)
            .GroupBy(l => l.MatchedItemId)
            .Select(g => new { CatalogItemId = g.Key, Line = g.OrderBy(l => l.LineNo).First() })
            .ToListAsync(cancellationToken);
        var sourceMap = sources.ToDictionary(s => s.CatalogItemId!.Value, s => s.Line);

        var result = items.Select(i =>
        {
            sourceMap.TryGetValue(i.Id, out var srcLine);
            return new
            {
                i.Id, i.ErpItemCode, i.Name, i.Uom, i.AutoCreatedAt,
                DocumentId = srcLine?.DocumentId,
                InvoiceNo = srcLine?.Document?.InvoiceNo ?? srcLine?.Document?.Filename,
                LineNo = srcLine?.LineNo
            };
        });
        return Ok(result);
    }

    [HttpPut("catalog/new-items/{id:guid}/accept")]
    public async Task<IActionResult> AcceptNewItem(Guid id, [FromBody] AcceptNewItemRequest request, CancellationToken cancellationToken)
    {
        var item = await db.CatalogItems.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (item is null) return NotFound();

        item.ErpItemCode = request.ErpItemCode;
        item.Name = request.Name;
        item.Uom = request.Uom;
        item.IsAutoCreated = false; // accepted — hide from proposed list immediately

        // Create a catalog job for the robot to process
        var payload = new CatalogItemPayload(Guid.NewGuid(), item.Id, item.ErpItemCode, item.Name, item.Uom);
        var job = new Models.CatalogJob
        {
            Id = payload.CatalogJobId,
            CatalogItemId = item.Id,
            BatchId = Guid.NewGuid().ToString("N"),
            Status = Models.CatalogJobStatus.Queued,
            RequestJson = System.Text.Json.JsonSerializer.Serialize(payload)
        };
        db.CatalogJobs.Add(job);

        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { item.Id, item.ErpItemCode, item.Name, jobId = job.Id });
    }

    [HttpDelete("catalog/new-items/{id:guid}")]
    public async Task<IActionResult> RejectNewItem(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.CatalogItems.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (item is null) return NotFound();

        // Clear matches on invoice lines that referenced this item
        var linkedLines = await db.InvoiceLines.Where(l => l.MatchedItemId == id).ToListAsync(cancellationToken);
        foreach (var line in linkedLines)
        {
            line.MatchedItemId = null;
            line.MatchConfidence = 0;
            line.MatchReason = "rejected";
        }

        item.Active = false;
        item.IsAutoCreated = false;
        await db.SaveChangesAsync(cancellationToken);
        return Ok();
    }

    [HttpGet("warehouses")]
    public async Task<IActionResult> SearchWarehouses([FromQuery] string? q, CancellationToken cancellationToken)
    {
        var query = db.Warehouses.Where(w => w.Active).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q) && q.Length >= 2)
        {
            var term = q.ToLowerInvariant();
            query = query.Where(w => w.Code.ToLower().Contains(term) || w.Name.ToLower().Contains(term));
        }
        var items = await query.OrderBy(w => w.Code).Take(30).Select(w => new { w.Code, w.Name }).ToListAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("cost-centers")]
    public async Task<IActionResult> SearchCostCenters([FromQuery] string? q, CancellationToken cancellationToken)
    {
        var query = db.CostCenters.Where(c => c.Active).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q) && q.Length >= 2)
        {
            var term = q.ToLowerInvariant();
            query = query.Where(c => c.Code.ToLower().Contains(term) || c.Name.ToLower().Contains(term));
        }
        var items = await query.OrderBy(c => c.Code).Take(30).Select(c => new { c.Code, c.Name }).ToListAsync(cancellationToken);
        return Ok(items);
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
