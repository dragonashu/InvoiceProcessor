using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using InvoiceProcessor.Web.Services.Extraction;
using InvoiceProcessor.Web.Services.Matching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace InvoiceProcessor.Web.Pages.Config;

public class IndexModel(AppDbContext db, IMatchingEngine matchingEngine, IExtractionPipeline extractionPipeline) : PageModel
{
    public List<Supplier> Suppliers { get; set; } = [];
    public int CatalogItemCount { get; set; }
    public int MappingCount { get; set; }
    public int WarehouseCount { get; set; }
    public int CostCenterCount { get; set; }
    public int ProposedItemCount { get; set; }
    public int ItemClassCount { get; set; }
    public int DviCount { get; set; }
    public CatalogImportLog? LastCatalogImport { get; set; }
    public string? Message { get; set; }

    [BindProperty] public Guid? EditId { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? ErpName { get; set; }
    [BindProperty] public string? VatNo { get; set; }
    [BindProperty] public string? Country { get; set; }
    [BindProperty] public string? Aliases { get; set; }
    [BindProperty] public string InvoiceType { get; set; } = "Intern";
    [BindProperty] public string TaxationType { get; set; } = "TaxareNormala";
    [BindProperty] public string TransactionType { get; set; } = "TranzactieInterna";

    [BindProperty] public IFormFile? CatalogFile { get; set; }
    [BindProperty] public IFormFile? WarehouseFile { get; set; }
    [BindProperty] public IFormFile? CostCenterFile { get; set; }
    [BindProperty] public IFormFile? ItemClassFile { get; set; }

    public async Task OnGetAsync(string? message, CancellationToken ct)
    {
        Message = message;
        Suppliers = await db.Suppliers.Where(s => s.Active).OrderBy(s => s.Name).ToListAsync(ct);
        CatalogItemCount = await db.CatalogItems.CountAsync(c => c.Active, ct);
        MappingCount = await db.SupplierItemMappings.CountAsync(m => m.Active, ct);
        WarehouseCount = await db.Warehouses.CountAsync(w => w.Active, ct);
        CostCenterCount = await db.CostCenters.CountAsync(c => c.Active, ct);
        ProposedItemCount = await db.CatalogItems.CountAsync(c => c.IsAutoCreated && c.AcceptedAt == null && c.Active, ct);
        ItemClassCount = await db.ItemClasses.CountAsync(ic => ic.Active, ct);
        DviCount = await db.CustomsDeclarations.CountAsync(ct);
        LastCatalogImport = await db.CatalogImportLogs
            .OrderByDescending(l => l.ImportedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IActionResult> OnPostImportDviAsync([FromServices] Services.Extraction.IDviFolderScanner scanner, CancellationToken ct)
    {
        var added = await scanner.ScanAsync(ct);
        return RedirectToPage(new { message = $"DVI: {added} importate." });
    }

    public async Task<IActionResult> OnPostClearDviAsync(CancellationToken ct)
    {
        // Detach documents first, then delete all declarations and the on-disk copies.
        var attached = await db.Documents.Where(d => d.CustomsDeclarationId != null).ToListAsync(ct);
        foreach (var d in attached) d.CustomsDeclarationId = null;

        var dvis = await db.CustomsDeclarations.ToListAsync(ct);
        foreach (var dvi in dvis)
        {
            try { if (System.IO.File.Exists(dvi.StoragePath)) System.IO.File.Delete(dvi.StoragePath); }
            catch { /* best-effort cleanup */ }
        }
        db.CustomsDeclarations.RemoveRange(dvis);
        await db.SaveChangesAsync(ct);
        return RedirectToPage(new { message = $"{dvis.Count} DVI sterse. Poti reimporta din folder." });
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
            supplier.InvoiceType = Enum.TryParse<Enums.InvoiceType>(InvoiceType, out var it) ? it : Enums.InvoiceType.Intern;
            supplier.TaxationType = Enum.TryParse<Enums.TaxationType>(TaxationType, out var tt) ? tt : Enums.TaxationType.TaxareNormala;
            supplier.TransactionType = Enum.TryParse<Enums.TransactionType>(TransactionType, out var trt) ? trt : Enums.TransactionType.TranzactieInterna;
        }
        else
        {
            db.Suppliers.Add(new Supplier
            {
                Name = Name,
                ErpName = string.IsNullOrWhiteSpace(ErpName) ? null : ErpName,
                VatNo = string.IsNullOrWhiteSpace(VatNo) ? null : VatNo,
                Country = string.IsNullOrWhiteSpace(Country) ? null : Country,
                AliasesJson = aliasesJson,
                InvoiceType = Enum.TryParse<Enums.InvoiceType>(InvoiceType, out var it2) ? it2 : Enums.InvoiceType.Intern,
                TaxationType = Enum.TryParse<Enums.TaxationType>(TaxationType, out var tt2) ? tt2 : Enums.TaxationType.TaxareNormala,
                TransactionType = Enum.TryParse<Enums.TransactionType>(TransactionType, out var trt2) ? trt2 : Enums.TransactionType.TranzactieInterna
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

        // Remove all catalog jobs
        var catalogJobs = await db.CatalogJobs.ToListAsync(ct);
        db.CatalogJobs.RemoveRange(catalogJobs);

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
        return RedirectToPage(new { message = $"{documents.Count} document(e), {jobs.Count} posting job(uri) si {catalogJobs.Count} catalog job(uri) sterse. Furnizorii si catalogul raman intacte." });
    }

    public async Task<IActionResult> OnPostImportCatalogAsync(CancellationToken ct)
    {
        if (CatalogFile is null || CatalogFile.Length == 0)
            return RedirectToPage(new { message = "Selecteaza un fisier XLSX." });

        await using var stream = CatalogFile.OpenReadStream();
        var (added, updated) = await matchingEngine.ImportCatalogXlsxAsync(
            stream, CatalogImportSource.Manual, CatalogFile.FileName, ct);
        return RedirectToPage(new { message = $"Catalog importat: {added} articole noi, {updated} actualizate." });
    }

    public async Task<IActionResult> OnPostImportWarehousesAsync(CancellationToken ct)
    {
        if (WarehouseFile is null || WarehouseFile.Length == 0)
            return RedirectToPage(new { message = "Selecteaza un fisier XLSX." });

        await using var stream = WarehouseFile.OpenReadStream();
        var rows = InvoiceProcessor.Web.Services.Extraction.XlsxReader.ReadRows(stream);
        if (rows.Count < 2) return RedirectToPage(new { message = "Fisier gol." });

        var header = rows[0];
        var gestIdx = Array.FindIndex(header, h => string.Equals(h?.Trim(), "Gestiune", StringComparison.OrdinalIgnoreCase));
        if (gestIdx < 0) gestIdx = 1; // fallback: column B

        var existing = await db.Warehouses.ToDictionaryAsync(w => w.Code, ct);
        int added = 0, updated = 0;
        for (var i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Length == 0 || gestIdx >= r.Length) continue;
            var name = r[gestIdx]?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (existing.TryGetValue(name, out var w)) { w.Name = name; w.Active = true; updated++; }
            else { db.Warehouses.Add(new Warehouse { Code = name, Name = name }); existing[name] = new Warehouse(); added++; }
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

    public async Task<IActionResult> OnPostImportItemClassesAsync(CancellationToken ct)
    {
        if (ItemClassFile is null || ItemClassFile.Length == 0)
            return RedirectToPage(new { message = "Selecteaza un fisier XLSX." });

        await using var stream = ItemClassFile.OpenReadStream();
        var rows = InvoiceProcessor.Web.Services.Extraction.XlsxReader.ReadRows(stream);

        int headerIdx = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var hasNrCrt = r.Any(c => string.Equals(c?.Trim(), "NrCrt", StringComparison.OrdinalIgnoreCase));
            var hasDenumire = r.Any(c => string.Equals(c?.Trim(), "Denumire", StringComparison.OrdinalIgnoreCase));
            if (hasNrCrt && hasDenumire) { headerIdx = i; break; }
        }
        if (headerIdx < 0)
            return RedirectToPage(new { message = "Antetul (NrCrt + Denumire) nu a fost gasit." });

        var headers = rows[headerIdx];
        int FindCol(params string[] names) =>
            Array.FindIndex(headers, h => names.Any(n => string.Equals(h?.Trim(), n, StringComparison.OrdinalIgnoreCase)));
        var nameIdx = FindCol("Denumire");
        var symbolIdx = FindCol("Simbol");
        var levelIdx = FindCol("Nivel");
        if (nameIdx < 0) return RedirectToPage(new { message = "Coloana Denumire nu a fost gasita." });

        var existing = await db.ItemClasses.ToDictionaryAsync(ic => ic.Name, ct);
        int added = 0, updated = 0;
        for (var i = headerIdx + 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Length == 0) continue;
            var name = nameIdx < r.Length ? r[nameIdx]?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var symbol = symbolIdx >= 0 && symbolIdx < r.Length ? r[symbolIdx]?.Trim() : null;
            int? level = null;
            if (levelIdx >= 0 && levelIdx < r.Length && int.TryParse(r[levelIdx], out var lvl)) level = lvl;

            if (existing.TryGetValue(name, out var cls))
            {
                cls.Symbol = string.IsNullOrWhiteSpace(symbol) ? cls.Symbol : symbol;
                cls.Level = level ?? cls.Level;
                cls.Active = true;
                updated++;
            }
            else
            {
                var ic = new ItemClass { Name = name, Symbol = string.IsNullOrWhiteSpace(symbol) ? null : symbol, Level = level, Active = true };
                db.ItemClasses.Add(ic);
                existing[name] = ic;
                added++;
            }
        }
        await db.SaveChangesAsync(ct);
        return RedirectToPage(new { message = $"Clase articole importate: {added} noi, {updated} actualizate." });
    }

    public async Task<IActionResult> OnPostClearProposedItemsAsync(CancellationToken ct)
    {
        // Only clear unaccepted proposed items; accepted ones have pending robot jobs
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE InvoiceLines SET MatchedItemId = NULL, MatchConfidence = 0, MatchReason = 'proposed-cleared' WHERE MatchedItemId IN (SELECT Id FROM CatalogItems WHERE IsAutoCreated = 1 AND AcceptedAt IS NULL)", ct);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM CatalogJobs WHERE CatalogItemId IN (SELECT Id FROM CatalogItems WHERE IsAutoCreated = 1 AND AcceptedAt IS NULL)", ct);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM SupplierItemMappings WHERE CatalogItemId IN (SELECT Id FROM CatalogItems WHERE IsAutoCreated = 1 AND AcceptedAt IS NULL)", ct);
        var count = await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM CatalogItems WHERE IsAutoCreated = 1 AND AcceptedAt IS NULL", ct);
        return RedirectToPage(new { message = $"{count} articol(e) propuse sterse." });
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

    public async Task<IActionResult> OnPostReextractAsync(CancellationToken ct)
    {
        // Re-run the full extraction pipeline so canonical JSON picks up any extractor changes,
        // then re-match against the current catalog.
        var documents = await db.Documents
            .Where(d => d.Status == DocumentStatus.NeedsReview
                     || d.Status == DocumentStatus.Matched
                     || d.Status == DocumentStatus.Validated
                     || d.Status == DocumentStatus.ReadyToPost
                     || d.Status == DocumentStatus.Classified
                     || d.Status == DocumentStatus.Extracted
                     || d.Status == DocumentStatus.Failed)
            .Select(d => d.Id)
            .ToListAsync(ct);

        var count = 0;
        foreach (var docId in documents)
        {
            await extractionPipeline.ReextractDocumentAsync(docId, ct);
            count++;
        }

        return RedirectToPage(new { message = $"{count} document(e) re-extrase si re-asociate." });
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
