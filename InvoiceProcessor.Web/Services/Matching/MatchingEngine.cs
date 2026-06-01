using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using InvoiceProcessor.Web.Services.Extraction;
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

        var document = await db.Documents.Include(d => d.Supplier).FirstAsync(x => x.Id == documentId, cancellationToken);
        var mappings = document.SupplierId.HasValue
            ? await db.SupplierItemMappings.Where(x => x.SupplierId == document.SupplierId && x.Active).Include(x => x.CatalogItem).ToListAsync(cancellationToken)
            : [];
        var catalog = await db.CatalogItems.Where(c => c.Active).ToListAsync(cancellationToken);

        // Aliplast rule: match ONLY by exact CodIntern ↔ ErpItemCode; otherwise auto-create.
        var strictCodeOnly = IsStrictCodeMatchSupplier(document.Supplier, invoice.Supplier);
        // Yildiz rule: description-only token match (>= 0.80). Below → propose new item with ErpItemCode="default".
        var descriptionOnlyMatch = IsDescriptionOnlySupplier(document.Supplier, invoice.Supplier);

        // PRIMARY lookup: by ErpItemCode (= Cod Intern from nomenclator)
        // When multiple items share the same code, keep ALL of them for description-based disambiguation
        var catalogByCode = catalog
            .Where(c => !string.IsNullOrWhiteSpace(c.ErpItemCode))
            .GroupBy(c => NormalizeCode(c.ErpItemCode))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Secondary lookup for strict-match suppliers: some catalog rows store the short
        // item code (e.g. "EF1222") in the Name column rather than Cod Intern.
        var catalogByName = catalog
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => NormalizeCode(c.Name))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Pre-tokenize catalog items for description fallback.
        // Exclude "default"-coded items — these are transient Yildiz proposals, not real catalog entries,
        // so matching against them would pile new invoices onto a placeholder instead of the real ERP item.
        var catalogTokens = catalog
            .Where(c => c.ErpItemCode != "default")
            .Select(c => (Item: c, Tokens: Tokenize(c.Name), Dimensions: ExtractDimensions(c.Name)))
            .ToList();

        var lineNo = 1;
        int autoCreatedCount = 0;
        foreach (var line in invoice.Lines)
        {
            CatalogItem? matched = null;
            decimal confidence = 0.3m;
            string reason = "no-match";

            // Tier 1: Exact vendor code mapping — skipped in strict (Aliplast) or description-only (Yildiz) modes
            if (!strictCodeOnly && !descriptionOnlyMatch && !string.IsNullOrWhiteSpace(line.CodIntern))
            {
                var mapping = mappings.FirstOrDefault(m => m.VendorCode == line.CodIntern);
                if (mapping is not null)
                {
                    matched = mapping.CatalogItem;
                    confidence = 1.0m;
                    reason = "exact-vendor-code";
                }
            }

            // Tier 2: CODE-FIRST - match CodIntern against catalog ErpItemCode (skipped for Yildiz)
            if (matched is null && !descriptionOnlyMatch && !string.IsNullOrWhiteSpace(line.CodIntern))
            {
                var normalizedVendor = NormalizeCode(line.CodIntern);
                if (catalogByCode.TryGetValue(normalizedVendor, out var codeMatches))
                {
                    // If multiple items share the same code, pick the best by description similarity
                    matched = PickBestByDescription(codeMatches, line.DescriptionRaw, catalogTokens);
                    confidence = 0.95m;
                    reason = "code-match";
                }
                else if (strictCodeOnly && catalogByName.TryGetValue(normalizedVendor, out var nameMatches))
                {
                    // Aliplast-style fallback: the short code sits in the Denumire (Name) column
                    matched = PickBestByDescription(nameMatches, line.DescriptionRaw, catalogTokens);
                    confidence = 0.95m;
                    reason = "name-match";
                }
                else if (!strictCodeOnly)
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

                // Auto-learn mapping for future (skip in strict mode — the code match is already exact)
                if (!strictCodeOnly && matched != null && document.SupplierId.HasValue)
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

            // Tier 3: Token-based fuzzy match on DESCRIPTION — skipped only for Aliplast strict mode
            if (matched is null && !strictCodeOnly && catalogTokens.Count > 0)
            {
                var tokenThreshold = descriptionOnlyMatch ? 0.70m : 0.60m;
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

                if (bestItem is not null && bestScore >= tokenThreshold)
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

            // Tier 4: AUTO-CREATE — no match found, create a new catalog item.
            // Yildiz (description-only) uses the literal "default" code; ERP will generate the real one.
            if (matched is null && descriptionOnlyMatch && !string.IsNullOrWhiteSpace(line.DescriptionRaw))
            {
                var name = line.DescriptionRaw.Trim();
                if (name.Length > 250) name = name[..250];
                var newItem = new CatalogItem
                {
                    ErpItemCode = "default",
                    Name = name,
                    Uom = line.Uom,
                    IsAutoCreated = true,
                    AutoCreatedAt = DateTime.UtcNow,
                    Active = true
                };
                db.CatalogItems.Add(newItem);
                catalog.Add(newItem);
                matched = newItem;
                confidence = 0.50m;
                reason = "yildiz-new-item";
                autoCreatedCount++;
            }
            else if (matched is null)
            {
                var itemKey = !string.IsNullOrWhiteSpace(line.CodIntern)
                    ? line.CodIntern
                    : GenerateDescriptionKey(line.DescriptionRaw);

                if (!string.IsNullOrWhiteSpace(itemKey))
                {
                    var normalizedKey = NormalizeCode(itemKey);

                    // Check if this code already exists in catalog (active OR inactive).
                    // For strict-match suppliers (Aliplast), also look in the Name column
                    // because the short code may be stored there instead of Cod Intern.
                    var alreadyExists = catalog.FirstOrDefault(c =>
                        NormalizeCode(c.ErpItemCode) == normalizedKey ||
                        (strictCodeOnly && !string.IsNullOrWhiteSpace(c.Name) && NormalizeCode(c.Name) == normalizedKey));

                    // Also check the DB for inactive/rejected items not in memory
                    alreadyExists ??= strictCodeOnly
                        ? await db.CatalogItems.FirstOrDefaultAsync(c => c.ErpItemCode == itemKey || c.Name == itemKey, cancellationToken)
                        : await db.CatalogItems.FirstOrDefaultAsync(c => c.ErpItemCode == itemKey, cancellationToken);

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
                ExternalCode = line.ExternalCode,
                PropertyClass = line.PropertyClass,
                MatchedItemId = matched?.Id,
                MatchConfidence = confidence,
                MatchReason = reason
            });
        }

        // Match confidence no longer gates the invoice: new items (auto-created proposals)
        // are reviewed in the send-time modal, not by parking the whole invoice in review.
        // Only a low-confidence match against an EXISTING catalog item still needs a human.
        string[] newItemReasons = ["auto-created", "yildiz-new-item"];
        var hasUncertainMatch = db.InvoiceLines.Local
            .Where(x => x.DocumentId == documentId)
            .Any(x => x.MatchConfidence < 0.75m && !newItemReasons.Contains(x.MatchReason));
        document.Status = hasUncertainMatch ? DocumentStatus.NeedsReview : DocumentStatus.ReadyToPost;

        // Block transfer when the extraction sanity checks fail (dropped lines / total mismatch).
        document.TransferBlocked = !ExtractionChecks.Evaluate(invoice).TransferAllowed;

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

    private static bool IsDescriptionOnlySupplier(Supplier? supplier, string? canonicalSupplierName)
    {
        if (supplier?.Name != null && supplier.Name.Contains("yildiz", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(canonicalSupplierName) && canonicalSupplierName.Contains("yildiz", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static bool IsStrictCodeMatchSupplier(Supplier? supplier, string? canonicalSupplierName)
    {
        if (supplier?.VatNo != null && supplier.VatNo.Contains("PL9462354607", StringComparison.OrdinalIgnoreCase))
            return true;
        if (supplier?.Name != null && supplier.Name.Contains("aliplast", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(canonicalSupplierName) && canonicalSupplierName.Contains("aliplast", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

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

    public async Task<(int added, int updated)> ImportCatalogXlsxAsync(Stream stream, CatalogImportSource source, string? fileName, CancellationToken cancellationToken)
    {
        var rows = XlsxReader.ReadRows(stream);

        // Find header row: the one that contains both CODOBIECT and "Cod Intern"
        int headerIdx = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var hasCodObiect = r.Any(c => string.Equals(c?.Trim(), "CODOBIECT", StringComparison.OrdinalIgnoreCase));
            var hasCodIntern = r.Any(c => string.Equals(c?.Trim(), "Cod Intern", StringComparison.OrdinalIgnoreCase));
            if (hasCodObiect && hasCodIntern) { headerIdx = i; break; }
        }
        if (headerIdx < 0)
        {
            logger.LogWarning("XLSX import: header row (CODOBIECT + Cod Intern) not found");
            return (0, 0);
        }

        var headers = rows[headerIdx];
        int FindCol(params string[] candidates) =>
            Array.FindIndex(headers, h => candidates.Any(c => string.Equals(h?.Trim(), c, StringComparison.OrdinalIgnoreCase)));

        var uomIdx = FindCol("UM");
        var codeIdx = FindCol("Cod Intern", "CodIntern");
        var nameIdx = FindCol("Denumire", "Denumire obiect");

        if (codeIdx < 0 || nameIdx < 0)
        {
            logger.LogWarning("XLSX import: required columns missing. Headers: {Headers}", string.Join(", ", headers));
            return (0, 0);
        }

        await db.Database.ExecuteSqlRawAsync("UPDATE InvoiceLines SET MatchedItemId = NULL, MatchConfidence = 0, MatchReason = 'catalog-reset' WHERE MatchedItemId IS NOT NULL", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM SupplierItemMappings", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM CatalogJobs", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM CatalogItems", cancellationToken);

        var existingItems = new Dictionary<string, CatalogItem>();
        int added = 0, updated = 0;
        for (var i = headerIdx + 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Length == 0) continue;

            var code = codeIdx < r.Length ? r[codeIdx]?.Trim() : null;
            var name = nameIdx < r.Length ? r[nameIdx]?.Trim() : null;
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) continue;

            var uom = uomIdx >= 0 && uomIdx < r.Length ? r[uomIdx]?.Trim() : null;

            if (existingItems.TryGetValue(code, out var existing))
            {
                if (existing.Name != name || existing.Uom != uom)
                {
                    existing.Name = name;
                    existing.Uom = string.IsNullOrWhiteSpace(uom) ? existing.Uom : uom;
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
                    Active = true
                };
                db.CatalogItems.Add(item);
                existingItems[code] = item;
                added++;
            }
        }

        db.CatalogImportLogs.Add(new CatalogImportLog
        {
            ImportedAt = DateTime.UtcNow,
            Source = source,
            AddedCount = added,
            UpdatedCount = updated,
            FileName = fileName
        });

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Catalog import ({Source}): {Added} added, {Updated} updated", source, added, updated);
        return (added, updated);
    }

    // --- Token-based matching helpers ---

    internal static HashSet<string> Tokenize(string text)
    {
        // Normalize: lowercase, convert European decimal comma in numbers to a dot (so "90,5" -> "90.5")
        var normalized = text.ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"(\d),(\d)", "$1.$2");

        // Drop the "IGU :" prefix that appears on Yildiz lines but not on the ERP catalog names.
        normalized = Regex.Replace(normalized, @"\bigu\s*:?\s*", " ");

        // Strip known noise patterns (customs codes, alum-natur markers, 8-digit intrastat codes)
        normalized = NoisePattern.Replace(normalized, " ");

        // Normalize common Romanian/English spelling variants BEFORE splitting so tokens align.
        //   polyuretane (RO) ↔ polyurethane (EN); "low-e", "low e", "lowe" -> "lowe"; "arg(on)" -> "ar".
        normalized = Regex.Replace(normalized, @"polyuretan[e]?", "polyurethane");
        normalized = Regex.Replace(normalized, @"low[\s\-]*e", "lowe");
        normalized = Regex.Replace(normalized, @"\barg(on)?\b", "ar");

        // Dots outside a numeric context (e.g. the "." in "ARG.90%") become spaces; "44.1" stays intact.
        normalized = Regex.Replace(normalized, @"(?<!\d)\.|\.(?!\d)", " ");

        // Replace all remaining non-alphanumeric chars (incl. `/;,()[]{}%#-`) with spaces.
        normalized = Regex.Replace(normalized, @"[^a-z0-9.]+", " ");

        // Insert boundary spaces so fused "lowe3"/"3mm" split into ["lowe","3"] / ["3","mm"].
        normalized = Regex.Replace(normalized, @"([a-z])(\d)", "$1 $2");
        normalized = Regex.Replace(normalized, @"(\d)([a-z])", "$1 $2");

        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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

}
