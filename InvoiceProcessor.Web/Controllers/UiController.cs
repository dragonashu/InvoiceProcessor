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

    [HttpGet("documents/{id:guid}/new-items")]
    public async Task<IActionResult> GetDocumentNewItems(Guid id, CancellationToken cancellationToken)
    {
        var lines = await db.InvoiceLines
            .Where(l => l.DocumentId == id && l.MatchedItem != null
                        && l.MatchedItem.IsAutoCreated && l.MatchedItem.AcceptedAt == null
                        && l.MatchedItem.Active)
            .Include(l => l.MatchedItem)
            .OrderBy(l => l.LineNo)
            .ToListAsync(cancellationToken);

        // One row per distinct auto-created catalog item; keep the first source line.
        var result = lines
            .GroupBy(l => l.MatchedItemId!.Value)
            .Select(g =>
            {
                var line = g.OrderBy(l => l.LineNo).First();
                var item = line.MatchedItem!;
                return new
                {
                    item.Id,
                    item.ErpItemCode,
                    item.Name,
                    item.Uom,
                    LineNo = line.LineNo,
                    ExternalCode = line.ExternalCode,
                    PropertyClass = line.PropertyClass
                };
            })
            .ToList();

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
        item.AcceptedAt = DateTime.UtcNow; // stays IsAutoCreated=true until confirmed by CSV import

        // Load every invoice line matched to this item. ExternalCode/PropertyClass live on
        // the line (not the catalog item), so edits made in the popup must be written back
        // here — otherwise the posted invoice would carry the stale, pre-edit values.
        var matchedLines = await db.InvoiceLines
            .Where(l => l.MatchedItemId == item.Id)
            .OrderBy(l => l.LineNo)
            .ToListAsync(cancellationToken);
        var srcLine = matchedLines.FirstOrDefault();

        var externalCode = string.IsNullOrWhiteSpace(request.ExternalCode) ? srcLine?.ExternalCode : request.ExternalCode;
        var propertyClass = string.IsNullOrWhiteSpace(request.PropertyClass) ? srcLine?.PropertyClass : request.PropertyClass;

        // Persist the popup edits back onto the matched lines so the invoice posting
        // payload (which reads ExternalCode/PropertyClass from the line) stays in sync.
        foreach (var line in matchedLines)
        {
            if (!string.IsNullOrWhiteSpace(request.ExternalCode)) line.ExternalCode = request.ExternalCode;
            if (!string.IsNullOrWhiteSpace(request.PropertyClass)) line.PropertyClass = request.PropertyClass;
        }

        // Create a catalog job for the robot to process
        var payload = new CatalogItemPayload(Guid.NewGuid(), item.Id, item.ErpItemCode, item.Name, item.Uom, externalCode, propertyClass);
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

    [HttpGet("item-classes")]
    public async Task<IActionResult> GetItemClasses(CancellationToken cancellationToken)
    {
        var items = await db.ItemClasses
            .Where(ic => ic.Active)
            .OrderBy(ic => ic.Name)
            .Select(ic => ic.Name)
            .ToListAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("customs-declarations")]
    public async Task<IActionResult> GetCustomsDeclarations(CancellationToken cancellationToken)
    {
        var items = await db.CustomsDeclarations
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new { c.Id, c.Filename, c.Mrn, c.Lrn, c.ExchangeRate, c.ReleaseDate, c.InvoiceRef, c.CreatedAt })
            .ToListAsync(cancellationToken);
        return Ok(items);
    }

    [HttpPost("customs-declarations")]
    [Microsoft.AspNetCore.Mvc.RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> UploadCustomsDeclaration(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest("Fisier lipsa.");

        var dviFolder = Path.Combine(Directory.GetCurrentDirectory(), "Data", "dvi");
        Directory.CreateDirectory(dviFolder);
        var storagePath = Path.Combine(dviFolder, $"{Guid.NewGuid():N}_{Path.GetFileName(file.FileName)}");
        await using (var fs = System.IO.File.Create(storagePath))
            await file.CopyToAsync(fs, cancellationToken);

        var data = InvoiceProcessor.Web.Services.Extraction.CustomsDeclarationExtractor.Extract(storagePath);
        var dvi = new CustomsDeclaration
        {
            Filename = file.FileName,
            StoragePath = storagePath,
            Mrn = data.Mrn,
            Lrn = data.Lrn,
            ExchangeRate = data.ExchangeRate,
            ReleaseDate = data.ReleaseDate,
            InvoiceRef = data.InvoiceRef
        };
        db.CustomsDeclarations.Add(dvi);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { dvi.Id, dvi.Filename, dvi.Mrn, dvi.Lrn, dvi.ExchangeRate, dvi.ReleaseDate, dvi.InvoiceRef });
    }

    [HttpPost("documents/{id:guid}/customs-declaration")]
    public async Task<IActionResult> AttachCustomsDeclaration(Guid id, [FromBody] AttachCustomsRequest request, CancellationToken cancellationToken)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (doc is null) return NotFound();
        doc.CustomsDeclarationId = request.CustomsDeclarationId;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { doc.Id, doc.CustomsDeclarationId });
    }

    public record AttachCustomsRequest(Guid? CustomsDeclarationId);

    [HttpDelete("customs-declarations/{id:guid}")]
    public async Task<IActionResult> DeleteCustomsDeclaration(Guid id, CancellationToken cancellationToken)
    {
        var dvi = await db.CustomsDeclarations.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (dvi is null) return NotFound();

        var attached = await db.Documents.Where(d => d.CustomsDeclarationId == id).ToListAsync(cancellationToken);
        foreach (var d in attached) d.CustomsDeclarationId = null;

        try { if (System.IO.File.Exists(dvi.StoragePath)) System.IO.File.Delete(dvi.StoragePath); }
        catch { /* best-effort */ }

        db.CustomsDeclarations.Remove(dvi);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { id });
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
