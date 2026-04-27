using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Infrastructure;
using InvoiceProcessor.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InvoiceProcessor.Web.Services.Extraction;

public interface IDviFolderScanner
{
    Task<int> ScanAsync(CancellationToken cancellationToken);
}

public class DviFolderScanner(AppDbContext db, IOptions<AppOptions> options, ILogger<DviFolderScanner> logger) : IDviFolderScanner
{
    private readonly string _source = options.Value.Storage.DviFolder;
    private readonly string _store = options.Value.Storage.DviStoreRoot;

    public async Task<int> ScanAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_source)) return 0;
        Directory.CreateDirectory(_store);

        var known = (await db.CustomsDeclarations.Select(d => d.Filename).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int added = 0;
        foreach (var path in Directory.GetFiles(_source, "*.pdf", SearchOption.TopDirectoryOnly))
        {
            var filename = Path.GetFileName(path);
            if (known.Contains(filename)) continue;

            try
            {
                var dest = Path.Combine(_store, $"{Guid.NewGuid():N}_{filename}");
                File.Copy(path, dest, overwrite: true);
                var data = CustomsDeclarationExtractor.Extract(dest);
                var dvi = new CustomsDeclaration
                {
                    Filename = filename,
                    StoragePath = dest,
                    Mrn = data.Mrn,
                    Lrn = data.Lrn,
                    ExchangeRate = data.ExchangeRate,
                    ReleaseDate = data.ReleaseDate,
                    InvoiceRef = data.InvoiceRef
                };
                db.CustomsDeclarations.Add(dvi);

                // Auto-attach: if the DVI's InvoiceRef matches an unassigned document, link them.
                if (!string.IsNullOrWhiteSpace(dvi.InvoiceRef))
                {
                    var candidates = await db.Documents
                        .Where(d => d.InvoiceNo != null && d.CustomsDeclarationId == null)
                        .ToListAsync(cancellationToken);
                    var match = candidates.FirstOrDefault(d => InvoiceRefMatcher.Matches(d.InvoiceNo, dvi.InvoiceRef));
                    if (match != null)
                    {
                        match.CustomsDeclarationId = dvi.Id;
                        logger.LogInformation("DVI {File} auto-attached to document {Invoice}", filename, match.InvoiceNo);
                    }
                }

                known.Add(filename);
                added++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to ingest DVI file {Path}", path);
            }
        }

        if (added > 0) await db.SaveChangesAsync(cancellationToken);
        return added;
    }
}
