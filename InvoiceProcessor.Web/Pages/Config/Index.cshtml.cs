using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using InvoiceProcessor.Web.Services.Matching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Pages.Config;

public class IndexModel(AppDbContext db, IMatchingEngine matchingEngine) : PageModel
{
    public List<Supplier> Suppliers { get; set; } = [];
    public int CatalogItemCount { get; set; }
    public int MappingCount { get; set; }
    public int WarehouseCount { get; set; }
    public int CostCenterCount { get; set; }
    public string? Message { get; set; }

    [BindProperty] public Guid? EditId { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? ErpName { get; set; }
    [BindProperty] public string? VatNo { get; set; }
    [BindProperty] public string? Country { get; set; }
    [BindProperty] public string? Aliases { get; set; }

    [BindProperty] public IFormFile? CatalogFile { get; set; }
    [BindProperty] public IFormFile? WarehouseFile { get; set; }
    [BindProperty] public IFormFile? CostCenterFile { get; set; }

    public async Task OnGetAsync(string? message, CancellationToken ct)
    {
        Message = message;
        Suppliers = await db.Suppliers.Where(s => s.Active).OrderBy(s => s.Name).ToListAsync(ct);
        CatalogItemCount = await db.CatalogItems.CountAsync(c => c.Active, ct);
        MappingCount = await db.SupplierItemMappings.CountAsync(m => m.Active, ct);
        WarehouseCount = await db.Warehouses.CountAsync(w => w.Active, ct);
        CostCenterCount = await db.CostCenters.CountAsync(c => c.Active, ct);
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var aliasesJson = string.IsNullOrWhiteSpace(Aliases) ? "[]" : BuildAliasesJson(Aliases);

        if (EditId.HasValue && EditId != Guid.Empty)
        {
            var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == EditId, ct);
            if (supplier is null) return RedirectToPage(new { message = "Furnizorul nu a fost gasit." });

            supplier.Name = Name;
            supplier.ErpName = string.IsNullOrWhiteSpace(ErpName) ? null : ErpName;
            supplier.VatNo = string.IsNullOrWhiteSpace(VatNo) ? null : VatNo;
            supplier.Country = string.IsNullOrWhiteSpace(Country) ? null : Country;
            supplier.AliasesJson = aliasesJson;
        }
        else
        {
            db.Suppliers.Add(new Supplier
            {
                Name = Name,
                ErpName = string.IsNullOrWhiteSpace(ErpName) ? null : ErpName,
                VatNo = string.IsNullOrWhiteSpace(VatNo) ? null : VatNo,
                Country = string.IsNullOrWhiteSpace(Country) ? null : Country,
                AliasesJson = aliasesJson
            });
        }

        await db.SaveChangesAsync(ct);
        return RedirectToPage(new { message = "Furnizor salvat cu succes." });
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken ct)
    {
        if (!EditId.HasValue) return RedirectToPage();

        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == EditId, ct);
        if (supplier is not null)
        {
            supplier.Active = false;
            await db.SaveChangesAsync(ct);
        }

        return RedirectToPage(new { message = "Furnizor dezactivat." });
    }

    public async Task<IActionResult> OnPostClearInboxAsync(CancellationToken ct)
    {
        // Remove all posting jobs
        var jobs = await db.PostingJobs.ToListAsync(ct);
        db.PostingJobs.RemoveRange(jobs);

        // Remove all documents, extract artifacts, invoice lines, and audit events
        var documents = await db.Documents.ToListAsync(ct);
        var docIds = documents.Select(d => d.Id).ToList();

        var artifacts = await db.ExtractArtifacts.Where(a => docIds.Contains(a.DocumentId)).ToListAsync(ct);
        var lines = await db.InvoiceLines.Where(l => docIds.Contains(l.DocumentId)).ToListAsync(ct);
        var audits = await db.AuditEvents.Where(a => a.DocumentId.HasValue && docIds.Contains(a.DocumentId.Value)).ToListAsync(ct);

        db.AuditEvents.RemoveRange(audits);
        db.InvoiceLines.RemoveRange(lines);
        db.ExtractArtifacts.RemoveRange(artifacts);
        db.Documents.RemoveRange(documents);

        await db.SaveChangesAsync(ct);
        return RedirectToPage(new { message = $"{documents.Count} document(e) si {jobs.Count} job(uri) sterse. Furnizorii si catalogul raman intacte." });
    }

    public async Task<IActionResult> OnPostImportCatalogAsync(CancellationToken ct)
    {
        if (CatalogFile is null || CatalogFile.Length == 0)
            return RedirectToPage(new { message = "Selecteaza un fisier CSV." });

        await using var stream = CatalogFile.OpenReadStream();
        var (added, updated) = await matchingEngine.ImportCatalogCsvAsync(stream, ct);
        return RedirectToPage(new { message = $"Catalog importat: {added} articole noi, {updated} actualizate." });
    }

    public async Task<IActionResult> OnPostImportWarehousesAsync(CancellationToken ct)
    {
        if (WarehouseFile is null || WarehouseFile.Length == 0)
            return RedirectToPage(new { message = "Selecteaza un fisier CSV." });

        using var reader = new StreamReader(WarehouseFile.OpenReadStream());
        var headerLine = await reader.ReadLineAsync(ct);
        if (headerLine is null) return RedirectToPage(new { message = "Fisier gol." });

        var separator = headerLine.Contains('\t') ? '\t' : headerLine.Contains(';') ? ';' : ',';
        var headers = headerLine.Split(separator).Select(h => h.Trim().Trim('"').ToUpperInvariant()).ToList();
        var codeIdx = headers.FindIndex(h => h is "COD" or "CODE" or "CODGESTIUNE" or "WAREHOUSE");
        var nameIdx = headers.FindIndex(h => h is "DENUMIRE" or "NAME" or "NUME" or "DESCRIERE");
        if (codeIdx < 0 || nameIdx < 0) return RedirectToPage(new { message = "Coloanele COD si DENUMIRE nu au fost gasite." });

        var existing = await db.Warehouses.ToDictionaryAsync(w => w.Code, ct);
        int added = 0, updated = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(separator);
            if (parts.Length <= Math.Max(codeIdx, nameIdx)) continue;
            var code = parts[codeIdx].Trim().Trim('"');
            var name = parts[nameIdx].Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(code)) continue;

            if (existing.TryGetValue(code, out var w)) { w.Name = name; w.Active = true; updated++; }
            else { db.Warehouses.Add(new Warehouse { Code = code, Name = name }); existing[code] = new Warehouse(); added++; }
        }
        await db.SaveChangesAsync(ct);
        return RedirectToPage(new { message = $"Gestiuni importate: {added} noi, {updated} actualizate." });
    }

    public async Task<IActionResult> OnPostImportCostCentersAsync(CancellationToken ct)
    {
        if (CostCenterFile is null || CostCenterFile.Length == 0)
            return RedirectToPage(new { message = "Selecteaza un fisier CSV." });

        using var reader = new StreamReader(CostCenterFile.OpenReadStream());
        var headerLine = await reader.ReadLineAsync(ct);
        if (headerLine is null) return RedirectToPage(new { message = "Fisier gol." });

        var separator = headerLine.Contains('\t') ? '\t' : headerLine.Contains(';') ? ';' : ',';
        var headers = headerLine.Split(separator).Select(h => h.Trim().Trim('"').ToUpperInvariant()).ToList();
        var codeIdx = headers.FindIndex(h => h is "COD" or "CODE" or "CODCENTRUCOST" or "COSTCENTER");
        var nameIdx = headers.FindIndex(h => h is "DENUMIRE" or "NAME" or "NUME" or "DESCRIERE");
        if (codeIdx < 0 || nameIdx < 0) return RedirectToPage(new { message = "Coloanele COD si DENUMIRE nu au fost gasite." });

        var existing = await db.CostCenters.ToDictionaryAsync(c => c.Code, ct);
        int added = 0, updated = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(separator);
            if (parts.Length <= Math.Max(codeIdx, nameIdx)) continue;
            var code = parts[codeIdx].Trim().Trim('"');
            var name = parts[nameIdx].Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(code)) continue;

            if (existing.TryGetValue(code, out var c)) { c.Name = name; c.Active = true; updated++; }
            else { db.CostCenters.Add(new CostCenter { Code = code, Name = name }); existing[code] = new CostCenter(); added++; }
        }
        await db.SaveChangesAsync(ct);
        return RedirectToPage(new { message = $"Centre de cost importate: {added} noi, {updated} actualizate." });
    }

    public async Task<IActionResult> OnPostRematchAsync(CancellationToken ct)
    {
        // Re-run matching on all documents that have extract artifacts and are in reviewable states
        var documents = await db.Documents
            .Where(d => d.Status == DocumentStatus.NeedsReview
                     || d.Status == DocumentStatus.Matched
                     || d.Status == DocumentStatus.Validated
                     || d.Status == DocumentStatus.ReadyToPost)
            .Select(d => d.Id)
            .ToListAsync(ct);

        var rematched = 0;
        foreach (var docId in documents)
        {
            var artifact = await db.ExtractArtifacts.FirstOrDefaultAsync(a => a.DocumentId == docId, ct);
            if (artifact is null) continue;

            await matchingEngine.MatchInvoiceLinesAsync(docId, artifact.CanonicalJson, ct);
            rematched++;
        }

        return RedirectToPage(new { message = $"{rematched} document(e) re-asociate cu catalogul actualizat." });
    }

    private static string BuildAliasesJson(string csv)
    {
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return "[" + string.Join(",", parts.Select(p => $"\"{p.Replace("\"", "\\\"")}\"")) + "]";
    }

    public static string ParseAliases(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return "";
        var trimmed = json.Trim('[', ']');
        var parts = trimmed.Split(',').Select(p => p.Trim().Trim('"'));
        return string.Join(", ", parts);
    }
}
