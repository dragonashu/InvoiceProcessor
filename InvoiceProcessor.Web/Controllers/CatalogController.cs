using System.Diagnostics;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using InvoiceProcessor.Web.Services.Matching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Controllers;

[ApiController]
[Route("catalog")]
public class CatalogController(
    IMatchingEngine matchingEngine,
    AppDbContext db,
    IConfiguration config,
    IWebHostEnvironment env,
    ILogger<CatalogController> logger) : ControllerBase
{
    public record CatalogImportRequest(string FilePath);

    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] CatalogImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.FilePath))
            return BadRequest(new { error = "filePath is required" });

        if (!TryResolveAllowedPath(request.FilePath, out var resolved, out var reason))
            return BadRequest(new { error = reason, filePath = request.FilePath });

        if (!System.IO.File.Exists(resolved))
            return NotFound(new { error = "file not found", filePath = resolved });

        if (!resolved.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "only .xlsx files are accepted", filePath = resolved });

        var sw = Stopwatch.StartNew();
        int added, updated;
        try
        {
            // ImportCatalogXlsxAsync wipes CatalogItems / SupplierItemMappings / CatalogJobs
            // and unlinks every InvoiceLine.MatchedItemId, so any prior auto-created proposals
            // are removed before the fresh catalog goes in.
            await using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
            (added, updated) = await matchingEngine.ImportCatalogXlsxAsync(
                stream, CatalogImportSource.Api, Path.GetFileName(resolved), ct);
        }
        catch (IOException ex) when (IsLockError(ex))
        {
            logger.LogWarning(ex, "Catalog import: file is locked: {Path}", resolved);
            return StatusCode(StatusCodes.Status423Locked, new { error = "file is locked by another process", filePath = resolved });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Catalog import failed: {Path}", resolved);
            return UnprocessableEntity(new { error = "import failed", message = ex.Message, filePath = resolved });
        }

        if (added == 0 && updated == 0)
            return UnprocessableEntity(new { error = "no rows imported — header row (CODOBIECT + Cod Intern) probably missing", filePath = resolved });

        var rematched = await RematchAllAsync(ct);

        sw.Stop();
        logger.LogInformation("Catalog import via API: {Added} added, {Updated} updated, {Rematched} docs re-matched from {Path}",
            added, updated, rematched, resolved);

        return Ok(new
        {
            added,
            updated,
            rematched,
            durationMs = sw.ElapsedMilliseconds,
            filePath = resolved
        });
    }

    private async Task<int> RematchAllAsync(CancellationToken ct)
    {
        var documentIds = await db.Documents
            .Where(d => d.Status == DocumentStatus.NeedsReview
                     || d.Status == DocumentStatus.Matched
                     || d.Status == DocumentStatus.Validated
                     || d.Status == DocumentStatus.ReadyToPost)
            .Select(d => d.Id)
            .ToListAsync(ct);

        var rematched = 0;
        foreach (var docId in documentIds)
        {
            var artifact = await db.ExtractArtifacts.FirstOrDefaultAsync(a => a.DocumentId == docId, ct);
            if (artifact is null) continue;
            await matchingEngine.MatchInvoiceLinesAsync(docId, artifact.CanonicalJson, ct);
            rematched++;
        }
        return rematched;
    }

    private bool TryResolveAllowedPath(string requested, out string resolved, out string reason)
    {
        resolved = string.Empty;
        var allowedRaw = config["App:CatalogImport:AllowedFolder"];
        if (string.IsNullOrWhiteSpace(allowedRaw))
        {
            reason = "App:CatalogImport:AllowedFolder is not configured";
            return false;
        }

        // Resolve both paths against ContentRoot so a relative AllowedFolder works
        // ("./data/catalog-imports") and a relative requested path is sandboxed too.
        var allowedAbs = Path.GetFullPath(allowedRaw, env.ContentRootPath);
        var requestedAbs = Path.GetFullPath(requested, env.ContentRootPath);

        var allowedNorm = NormalizeFolder(allowedAbs);
        var requestedNorm = requestedAbs.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!requestedNorm.StartsWith(allowedNorm, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"filePath must be inside the configured allowed folder ({allowedAbs})";
            return false;
        }

        resolved = requestedAbs;
        reason = string.Empty;
        return true;
    }

    private static string NormalizeFolder(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed + Path.DirectorySeparatorChar;
    }

    private static bool IsLockError(IOException ex)
    {
        // HResult 0x80070020 (sharing violation) / 0x80070021 (lock violation) on Windows
        var hr = ex.HResult & 0xFFFF;
        return hr == 0x20 || hr == 0x21;
    }
}
