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

        // Pre-build catalog lookup by InternalCode for code-first matching
        var catalogByCode = catalog
            .Where(c => !string.IsNullOrWhiteSpace(c.InternalCode))
            .GroupBy(c => NormalizeCode(c.InternalCode!))
            .ToDictionary(g => g.Key, g => g.First());

        // Pre-tokenize catalog items once (for description fallback)
        var catalogTokens = catalog.Select(c => (Item: c, Tokens: Tokenize(c.Name), Dimensions: ExtractDimensions(c.Name))).ToList();

        var lineNo = 1;
        foreach (var line in invoice.Lines)
        {
            CatalogItem? matched = null;
            decimal confidence = 0.3m;
            string reason = "no-match";

            // Tier 1: Exact vendor code mapping (from SupplierItemMappings)
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

            // Tier 2: CODE-FIRST - match VendorItemCode against catalog InternalCode
            if (matched is null && !string.IsNullOrWhiteSpace(line.VendorItemCode))
            {
                var normalizedVendor = NormalizeCode(line.VendorItemCode);
                if (catalogByCode.TryGetValue(normalizedVendor, out var codeMatch))
                {
                    matched = codeMatch;
                    confidence = 0.95m;
                    reason = "catalog-code-match";
                }
                else
                {
                    // Try partial code match: strip variant suffix (e.g., ACFA501/LAN -> ACFA501)
                    var baseCode = line.VendorItemCode.Contains('/')
                        ? NormalizeCode(line.VendorItemCode.Split('/')[0])
                        : null;
                    if (baseCode != null)
                    {
                        var partialMatch = catalogByCode
                            .Where(kv => kv.Key.StartsWith(baseCode) || baseCode.StartsWith(kv.Key))
                            .Select(kv => kv.Value)
                            .FirstOrDefault();
                        if (partialMatch != null)
                        {
                            matched = partialMatch;
                            confidence = 0.90m;
                            reason = "catalog-code-partial";
                        }
                    }
                }

                // Auto-learn: save mapping for future exact matches
                if (matched != null && document.SupplierId.HasValue)
                {
                    var existingMapping = mappings.FirstOrDefault(m => m.VendorCode == line.VendorItemCode);
                    if (existingMapping is null)
                    {
                        var newMapping = new SupplierItemMapping
                        {
                            SupplierId = document.SupplierId.Value,
                            VendorCode = line.VendorItemCode,
                            CatalogItemId = matched.Id
                        };
                        db.SupplierItemMappings.Add(newMapping);
                        mappings.Add(newMapping);
                        logger.LogInformation("Auto-mapped vendor code '{VendorCode}' -> catalog '{ErpCode}' (code-match) for supplier {SupplierId}",
                            line.VendorItemCode, matched.ErpItemCode, document.SupplierId);
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

                    // Auto-learn: if high confidence and vendor code exists, save mapping for future
                    if (bestScore >= 0.85m && !string.IsNullOrWhiteSpace(line.VendorItemCode) && document.SupplierId.HasValue)
                    {
                        var existingMapping = mappings.FirstOrDefault(m => m.VendorCode == line.VendorItemCode);
                        if (existingMapping is null)
                        {
                            var newMapping = new SupplierItemMapping
                            {
                                SupplierId = document.SupplierId.Value,
                                VendorCode = line.VendorItemCode,
                                CatalogItemId = bestItem.Id
                            };
                            db.SupplierItemMappings.Add(newMapping);
                            mappings.Add(newMapping);
                            logger.LogInformation("Auto-mapped vendor code '{VendorCode}' -> catalog '{ErpCode}' for supplier {SupplierId}",
                                line.VendorItemCode, bestItem.ErpItemCode, document.SupplierId);
                        }
                    }
                }
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

    private static string NormalizeCode(string code) =>
        code.Trim().ToUpperInvariant().Replace(" ", "");

    public async Task<(int added, int updated)> ImportCatalogCsvAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var headerLine = await reader.ReadLineAsync(cancellationToken);
        if (headerLine is null) return (0, 0);

        // Detect separator
        var separator = headerLine.Contains('\t') ? '\t' : headerLine.Contains(';') ? ';' : ',';
        var headers = headerLine.Split(separator).Select(h => h.Trim().Trim('"').ToUpperInvariant()).ToList();

        // Find column indices — flexible mapping
        var codeIdx = FindColumnIndex(headers, "CODOBIECT", "ERP_ITEM_CODE", "COD", "CODE", "ITEMCODE");
        var nameIdx = FindColumnIndex(headers, "DENUMIRE OBIECT", "DENUMIREOBIECT", "NAME", "DESCRIERE", "DESCRIPTION");
        var uomIdx = FindColumnIndex(headers, "UM", "UOM", "UNITATE");
        var taxIdx = FindColumnIndex(headers, "COTATVA", "COTA_TVA", "TAX_CODE", "TVA");
        var internalCodeIdx = FindColumnIndex(headers, "COD INTERN", "CODINTERN", "INTERNAL_CODE", "VENDOR_CODE");

        if (codeIdx < 0 || nameIdx < 0)
        {
            logger.LogWarning("CSV import: could not find required columns CODOBIECT and DENUMIRE OBIECT. Headers: {Headers}", string.Join(", ", headers));
            return (0, 0);
        }

        // Load existing items for upsert
        var existingItems = await db.CatalogItems.ToDictionaryAsync(c => c.ErpItemCode, cancellationToken);

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
            var internalCode = internalCodeIdx >= 0 && internalCodeIdx < parts.Count ? parts[internalCodeIdx].Trim().Trim('"') : null;

            if (existingItems.TryGetValue(code, out var existing))
            {
                if (existing.Name != name || existing.Uom != uom || existing.TaxCode != tax || existing.InternalCode != internalCode)
                {
                    existing.Name = name;
                    existing.Uom = string.IsNullOrWhiteSpace(uom) ? existing.Uom : uom;
                    existing.TaxCode = string.IsNullOrWhiteSpace(tax) ? existing.TaxCode : tax;
                    existing.InternalCode = string.IsNullOrWhiteSpace(internalCode) ? existing.InternalCode : internalCode;
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
                    InternalCode = string.IsNullOrWhiteSpace(internalCode) ? null : internalCode,
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
