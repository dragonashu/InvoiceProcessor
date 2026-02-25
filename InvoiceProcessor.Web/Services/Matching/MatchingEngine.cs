using System.Text.Json;
using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Services.Matching;

public class MatchingEngine(AppDbContext db, ILogger<MatchingEngine> logger) : IMatchingEngine
{
    public async Task MatchInvoiceLinesAsync(Guid documentId, string canonicalJson, CancellationToken cancellationToken)
    {
        var invoice = JsonSerializer.Deserialize<CanonicalInvoice>(canonicalJson);
        if (invoice is null) return;

        var existing = await db.InvoiceLines.Where(x => x.DocumentId == documentId).ToListAsync(cancellationToken);
        if (existing.Count > 0) db.InvoiceLines.RemoveRange(existing);

        var document = await db.Documents.FirstAsync(x => x.Id == documentId, cancellationToken);
        var mappings = await db.SupplierItemMappings.Where(x => x.SupplierId == document.SupplierId && x.Active).Include(x => x.CatalogItem).ToListAsync(cancellationToken);
        var catalog = await db.CatalogItems.Where(c => c.Active).ToListAsync(cancellationToken);

        var lineNo = 1;
        foreach (var line in invoice.Lines)
        {
            CatalogItem? matched = null;
            decimal confidence = 0.3m;
            string reason = "fuzzy-name";

            if (!string.IsNullOrWhiteSpace(line.VendorItemCode))
            {
                var mapping = mappings.FirstOrDefault(m => m.VendorCode == line.VendorItemCode);
                if (mapping is not null)
                {
                    matched = mapping.CatalogItem;
                    confidence = 1.0m;
                    reason = "exact-vendor-code";
                }
            }

            matched ??= catalog.FirstOrDefault(c => c.Name.Contains(line.DescriptionRaw, StringComparison.OrdinalIgnoreCase) || line.DescriptionRaw.Contains(c.Name, StringComparison.OrdinalIgnoreCase));
            if (matched is not null && confidence < 0.8m)
            {
                confidence = 0.8m;
            }

            db.InvoiceLines.Add(new InvoiceLine
            {
                DocumentId = documentId,
                LineNo = lineNo++,
                VendorCode = line.VendorItemCode,
                Description = line.DescriptionRaw,
                Qty = line.Qty,
                Uom = line.Uom,
                UnitPrice = line.UnitPrice,
                Amount = line.LineTotal,
                MatchedItemId = matched?.Id,
                MatchConfidence = confidence,
                MatchReason = reason
            });
        }

        var minConfidence = db.InvoiceLines.Local.Where(x => x.DocumentId == documentId).Select(x => x.MatchConfidence).DefaultIfEmpty(0m).Min();
        document.Status = minConfidence < 0.75m ? DocumentStatus.NeedsReview : DocumentStatus.ReadyToPost;

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Matched lines for {DocumentId}", documentId);
    }

    public async Task<int> ImportCatalogCsvAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var count = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var parts = line.Split(';', ',', '\t');
            if (parts.Length < 2 || parts[0].Equals("erp_item_code", StringComparison.OrdinalIgnoreCase)) continue;
            db.CatalogItems.Add(new CatalogItem { ErpItemCode = parts[0].Trim(), Name = parts[1].Trim(), Uom = parts.ElementAtOrDefault(2)?.Trim(), TaxCode = parts.ElementAtOrDefault(3)?.Trim(), Active = true });
            count++;
        }
        await db.SaveChangesAsync(cancellationToken);
        return count;
    }
}
