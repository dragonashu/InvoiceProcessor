using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Services.Matching;

public class MatchingEngine(AppDbContext db, ILogger<MatchingEngine> logger) : IMatchingEngine
{
    // Noise patterns to strip before tokenizing
    private static readonly Regex NoisePattern = new(
        @"\bCOD\.?INTRAST\.?\b|\bALUM\.?NATUR\b|\bAL\.?NATUR\b|\b\d{8}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Dimension pattern: e.g. 7x14.2, 5x36, 30.5
    private static readonly Regex DimensionPattern = new(
        @"\b(\d+(?:[.,]\d+)?)\s*[xX×]\s*(\d+(?:[.,]\d+)?)\b|\b(\d+[.,]\d+)\s*mm\b",
        RegexOptions.Compiled);

    public async Task MatchInvoiceLinesAsync(Guid documentId, string canonicalJson, CancellationToken cancellationToken)
    {
        var invoice = JsonSerializer.Deserialize<CanonicalInvoice>(canonicalJson);
        if (invoice is null) return;

        var existing = await db.InvoiceLines.Where(x => x.DocumentId == documentId).ToListAsync(cancellationToken);
        if (existing.Count > 0) db.InvoiceLines.RemoveRange(existing);

        var document = await db.Documents.FirstAsync(x => x.Id == documentId, cancellationToken);
        var mappings = document.SupplierId.HasValue
            ? await db.SupplierItemMappings.Where(x => x.SupplierId == document.SupplierId && x.Active).Include(x => x.CatalogItem).ToListAsync(cancellationToken)
            : [];
        var catalog = await db.CatalogItems.Where(c => c.Active).ToListAsync(cancellationToken);

        // PRIMARY lookup: by ErpItemCode (= Cod Intern from nomenclator)
        // When multiple items share the same code, keep ALL of them for description-based disambiguation
        var catalogByCode = catalog
            .Where(c => !string.IsNullOrWhiteSpace(c.ErpItemCode))
            .GroupBy(c => NormalizeCode(c.ErpItemCode))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Pre-tokenize catalog items for description fallback
        var catalogTokens = catalog.Select(c => (Item: c, Tokens: Tokenize(c.Name), Dimensions: ExtractDimensions(c.Name))).ToList();

        var lineNo = 1;
        int autoCreatedCount = 0;
        foreach (var line in invoice.Lines)
        {
            CatalogItem? matched = null;
            decimal confidence = 0.3m;
            string reason = "no-match";

            // Tier 1: Exact vendor code mapping (from SupplierItemMappings)
            if (!string.IsNullOrWhiteSpace(line.CodIntern))
            {
                var mapping = mappings.FirstOrDefault(m => m.VendorCode == line.CodIntern);
                if (mapping is not null)
                {
                    matched = mapping.CatalogItem;
                    confidence = 1.0m;
                    reason = "exact-vendor-code";
                }
            }

            // Tier 2: CODE-FIRST - match CodIntern against catalog ErpItemCode (= Cod Intern)
            if (matched is null && !string.IsNullOrWhiteSpace(line.CodIntern))
            {
                var normalizedVendor = NormalizeCode(line.CodIntern);
                if (catalogByCode.TryGetValue(normalizedVendor, out var codeMatches))
                {
                    // If multiple items share the same code, pick the best by description similarity
                    matched = PickBestByDescription(codeMatches, line.DescriptionRaw, catalogTokens);
                    confidence = 0.95m;
                    reason = "code-match";
                }
                else
                {
                    // Try partial: strip variant suffix (e.g., ACFA501/LAN -> ACFA501)
                    var baseCode = line.CodIntern.Contains('/')
                        ? NormalizeCode(line.CodIntern.Split('/')[0])
                        : null;
                    if (baseCode != null)
                    {
                        var partialCandidates = catalogByCode
                            .Where(kv => kv.Key.StartsWith(baseCode) || baseCode.StartsWith(kv.Key))
                            .SelectMany(kv => kv.Value)
                            .ToList();
                        if (partialCandidates.Count > 0)
                        {
                            matched = PickBestByDescription(partialCandidates, line.DescriptionRaw, catalogTokens);
                            confidence = 0.90m;
                            reason = "code-partial";
                        }
                    }
                }

                // Auto-learn mapping for future
                if (matched != null && document.SupplierId.HasValue)
                {
                    var existingMapping = mappings.FirstOrDefault(m => m.VendorCode == line.CodIntern);
                    if (existingMapping is null)
                    {
                        var newMapping = new SupplierItemMapping
                        {
                            SupplierId = document.SupplierId.Value,
                            VendorCode = line.CodIntern,
                            CatalogItemId = matched.Id
                        };
                        db.SupplierItemMappings.Add(newMapping);
                        mappings.Add(newMapping);
                    }
                }
            }

            // Tier 3: Token-based fuzzy match on DESCRIPTION (fallback)
            if (matched is null && catalogTokens.Count > 0)
            {
                var invoiceTokens = Tokenize(line.DescriptionRaw);
                var invoiceDims = ExtractDimensions(line.DescriptionRaw);

                var bestScore = 0m;
                CatalogItem? bestItem = null;

                foreach (var (item, catTokens, catDims) in catalogTokens)
                {
                    var score = ScoreMatch(invoiceTokens, catTokens, invoiceDims, catDims);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestItem = item;
                    }
                }

                if (bestItem is not null && bestScore >= 0.60m)
                {
                    matched = bestItem;
                    confidence = Math.Min(0.95m, bestScore);
                    reason = $"token-match({bestScore:F2})";

                    if (bestScore >= 0.85m && !string.IsNullOrWhiteSpace(line.CodIntern) && document.SupplierId.HasValue)
                    {
                        var existingMapping = mappings.FirstOrDefault(m => m.VendorCode == line.CodIntern);
                        if (existingMapping is null)
                        {
                            var newMapping = new SupplierItemMapping
                            {
                                SupplierId = document.SupplierId.Value,
                                VendorCode = line.CodIntern,
                                CatalogItemId = bestItem.Id
                            };
                            db.SupplierItemMappings.Add(newMapping);
                            mappings.Add(newMapping);
                        }
                    }
                }
            }

            // Tier 4: AUTO-CREATE — no match found, create a new catalog item
            // Use CodIntern as key if available, otherwise derive key from description
            if (matched is null)
            {
                var itemKey = !string.IsNullOrWhiteSpace(line.CodIntern)
                    ? line.CodIntern
                    : GenerateDescriptionKey(line.DescriptionRaw);

                if (!string.IsNullOrWhiteSpace(itemKey))
                {
                    var normalizedKey = NormalizeCode(itemKey);

                    // Check if this code already exists in catalog (active OR inactive)
                    var alreadyExists = catalog.FirstOrDefault(c =>
                        NormalizeCode(c.ErpItemCode) == normalizedKey);

                    // Also check the DB for inactive/rejected items not in memory
                    alreadyExists ??= await db.CatalogItems.FirstOrDefaultAsync(c =>
                        c.ErpItemCode == itemKey, cancellationToken);

                    if (alreadyExists != null)
                    {
                        // Reactivate if it was rejected
                        if (!alreadyExists.Active)
                        {
                            alreadyExists.Active = true;
                            alreadyExists.IsAutoCreated = true;
                            alreadyExists.AutoCreatedAt = DateTime.UtcNow;
                            if (!catalog.Contains(alreadyExists)) catalog.Add(alreadyExists);
                        }
                        matched = alreadyExists;
                    }
                    else
                    {
                        var newItem = new CatalogItem
                        {
                            ErpItemCode = itemKey,
                            Name = CapitalizeFirst(line.DescriptionRaw),
                            Uom = line.Uom,
                            IsAutoCreated = true,
                            AutoCreatedAt = DateTime.UtcNow,
                            Active = true
                        };
                        db.CatalogItems.Add(newItem);
                        catalog.Add(newItem);
                        catalogByCode[normalizedKey] = [newItem];
                        matched = newItem;
                        autoCreatedCount++;
                    }

                    confidence = 0.50m;
                    reason = "auto-created";
                }
            }

            db.InvoiceLines.Add(new InvoiceLine
            {
                DocumentId = documentId,
                LineNo = lineNo++,
                VendorCode = line.CodIntern,
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

        if (autoCreatedCount > 0)
            logger.LogInformation("Matched lines for {DocumentId}: {AutoCreated} new catalog items auto-created", documentId, autoCreatedCount);
        else
            logger.LogInformation("Matched lines for {DocumentId}", documentId);
    }

    private static CatalogItem PickBestByDescription(
        List<CatalogItem> candidates,
        string invoiceDescription,
        List<(CatalogItem Item, HashSet<string> Tokens, string? Dimensions)> catalogTokens)
    {
        if (candidates.Count == 1) return candidates[0];

        // Score each candidate by description token overlap
        var invoiceTokens = Tokenize(invoiceDescription);
        var invoiceDims = ExtractDimensions(invoiceDescription);

        CatalogItem? best = null;
        decimal bestScore = -1;
        foreach (var c in candidates)
        {
            var entry = catalogTokens.FirstOrDefault(ct => ct.Item.Id == c.Id);
            var score = entry.Tokens != null
                ? ScoreMatch(invoiceTokens, entry.Tokens, invoiceDims, entry.Dimensions)
                : 0m;
            if (score > bestScore) { bestScore = score; best = c; }
        }
        return best ?? candidates[0];
    }

    private static string NormalizeCode(string code) =>
        code.Trim().ToUpperInvariant().Replace(" ", "");

    /// <summary>
    /// Generates a stable short key from a description (for items without a vendor code).
    /// Truncates to first 80 chars to keep it manageable as an ErpItemCode.
    /// </summary>
    private static string? GenerateDescriptionKey(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var key = description.Trim();
        if (key.Length > 80) key = key[..80];
        return key;
    }

    private static string CapitalizeFirst(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var trimmed = text.Trim();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
    }

    public async Task<(int added, int updated)> ImportCatalogCsvAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var headerLine = await reader.ReadLineAsync(cancellationToken);
        if (headerLine is null) return (0, 0);

        // Detect separator
        var separator = headerLine.Contains('\t') ? '\t' : headerLine.Contains(';') ? ';' : ',';
        var headers = headerLine.TrimStart('\uFEFF').Split(separator).Select(h => h.Trim().Trim('"').ToUpperInvariant()).ToList();

        // Find column indices — flexible mapping
        // PRIMARY key: Cod Intern (supplier/vendor code used on invoices)
        var codeIdx = FindColumnIndex(headers, "COD INTERN", "CODINTERN", "INTERNAL_CODE", "VENDOR_CODE", "COD", "CODE");
        var nameIdx = FindColumnIndex(headers, "DENUMIRE OBIECT", "DENUMIREOBIECT", "NAME", "DESCRIERE", "DESCRIPTION");
        var uomIdx = FindColumnIndex(headers, "UM", "UOM", "UNITATE");
        var taxIdx = FindColumnIndex(headers, "COTATVA", "COTA_TVA", "TAX_CODE", "TVA");

        if (codeIdx < 0 || nameIdx < 0)
        {
            logger.LogWarning("CSV import: could not find required columns (Cod Intern / Denumire obiect). Headers: {Headers}", string.Join(", ", headers));
            return (0, 0);
        }

        // Wipe existing catalog with raw SQL (fast — avoids loading thousands of entities)
        await db.Database.ExecuteSqlRawAsync("UPDATE InvoiceLines SET MatchedItemId = NULL, MatchConfidence = 0, MatchReason = 'catalog-reset' WHERE MatchedItemId IS NOT NULL", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM SupplierItemMappings", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM CatalogJobs", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM CatalogItems", cancellationToken);

        var existingItems = new Dictionary<string, CatalogItem>();
        int added = 0, updated = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = ParseCsvLine(line, separator);
            if (parts.Count <= Math.Max(codeIdx, nameIdx)) continue;

            var code = parts[codeIdx].Trim().Trim('"');
            var name = parts[nameIdx].Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) continue;

            var uom = uomIdx >= 0 && uomIdx < parts.Count ? parts[uomIdx].Trim().Trim('"') : null;
            var tax = taxIdx >= 0 && taxIdx < parts.Count ? parts[taxIdx].Trim().Trim('"') : null;

            if (existingItems.TryGetValue(code, out var existing))
            {
                if (existing.Name != name || existing.Uom != uom || existing.TaxCode != tax)
                {
                    existing.Name = name;
                    existing.Uom = string.IsNullOrWhiteSpace(uom) ? existing.Uom : uom;
                    existing.TaxCode = string.IsNullOrWhiteSpace(tax) ? existing.TaxCode : tax;
                    existing.Active = true;
                    updated++;
                }
            }
            else
            {
                var item = new CatalogItem
                {
                    ErpItemCode = code,
                    Name = name,
                    Uom = string.IsNullOrWhiteSpace(uom) ? null : uom,
                    TaxCode = string.IsNullOrWhiteSpace(tax) ? null : tax,
                    Active = true
                };
                db.CatalogItems.Add(item);
                existingItems[code] = item;
                added++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Catalog import: {Added} added, {Updated} updated", added, updated);
        return (added, updated);
    }

    // --- Token-based matching helpers ---

    internal static HashSet<string> Tokenize(string text)
    {
        // Normalize: lowercase, replace European decimal comma with dot in numbers
        var normalized = text.ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"(\d),(\d)", "$1.$2");

        // Strip noise
        normalized = NoisePattern.Replace(normalized, " ");

        // Strip punctuation except dots in numbers, and x in dimensions
        normalized = Regex.Replace(normalized, @"[()/{}\[\]]", " ");

        // Tokenize by whitespace
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim(',', '.', ';', ':'))
            .Where(t => t.Length > 0)
            .ToHashSet();
    }

    internal static string? ExtractDimensions(string text)
    {
        var normalized = text.ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"(\d),(\d)", "$1.$2");
        var match = DimensionPattern.Match(normalized);
        return match.Success ? match.Value.Trim() : null;
    }

    internal static decimal ScoreMatch(HashSet<string> invoiceTokens, HashSet<string> catalogTokens, string? invoiceDims, string? catalogDims)
    {
        if (invoiceTokens.Count == 0 || catalogTokens.Count == 0) return 0m;

        var matchedCount = invoiceTokens.Count(t => catalogTokens.Contains(t));
        var maxCount = Math.Max(invoiceTokens.Count, catalogTokens.Count);
        var baseScore = (decimal)matchedCount / maxCount;

        // Dimension bonus/penalty
        if (invoiceDims is not null && catalogDims is not null)
        {
            if (invoiceDims == catalogDims)
                baseScore += 0.10m;
            else
                baseScore -= 0.20m;
        }

        return Math.Clamp(baseScore, 0m, 1.0m);
    }

    // --- CSV parsing helpers ---

    private static int FindColumnIndex(List<string> headers, params string[] candidates)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            var h = headers[i].Replace(" ", "");
            foreach (var candidate in candidates)
            {
                if (h.Equals(candidate.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }
        return -1;
    }

    private static List<string> ParseCsvLine(string line, char separator)
    {
        var parts = new List<string>();
        var inQuotes = false;
        var current = new System.Text.StringBuilder();

        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == separator && !inQuotes)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        parts.Add(current.ToString());
        return parts;
    }
}
