using System.Text.Json;
using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using InvoiceProcessor.Web.Services.Matching;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;

namespace InvoiceProcessor.Web.Services.Extraction;

public class PdfExtractionPipeline(
    AppDbContext db,
    IDocumentClassifier classifier,
    ICanonicalParser parser,
    IMatchingEngine matchingEngine,
    IInvoiceValidator invoiceValidator,
    IEnumerable<ISupplierInvoiceExtractor> supplierExtractors,
    ILogger<PdfExtractionPipeline> logger) : IExtractionPipeline
{
    public async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await db.Documents
            .Where(d => d.Status == DocumentStatus.Received || d.Status == DocumentStatus.Extracting)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in pending)
        {
            await ProcessDocumentAsync(id, cancellationToken);
        }
    }

    public async Task ProcessDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await db.Documents.Include(d => d.Supplier).FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null || document.Status == DocumentStatus.Duplicate || document.Status == DocumentStatus.Posted) return;

        document.Status = DocumentStatus.Extracting;
        await db.SaveChangesAsync(cancellationToken);

        PdfDocument pdf;
        string text;
        try
        {
            pdf = PdfDocument.Open(document.StoragePath);
            text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        }
        catch (Exception ex)
        {
            document.Status = DocumentStatus.Failed;
            db.AuditEvents.Add(new AuditEvent { DocumentId = document.Id, EventType = "EXTRACT_FAIL", Message = ex.Message });
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        using (pdf)
        {
            if (text.Length < 100)
            {
                document.Status = DocumentStatus.NeedsOcr;
                await SaveArtifact(document.Id, JsonSerializer.Serialize(new { textLength = text.Length, reason = "No embedded text" }), "{}", cancellationToken);
                return;
            }

            document.Status = DocumentStatus.Extracted;
            var cls = classifier.Classify(text);
            document.DocType = cls.type;
            document.Confidence = cls.confidence;
            document.Status = cls.confidence < 0.6m ? DocumentStatus.NeedsReview : DocumentStatus.Classified;

            var suppliers = await db.Suppliers.Where(s => s.Active).ToListAsync(cancellationToken);
            var supplier = suppliers.FirstOrDefault(s => !string.IsNullOrWhiteSpace(cls.vatNo) && s.VatNo == cls.vatNo)
                           ?? suppliers.FirstOrDefault(s => cls.supplierName is not null && (s.Name.Contains(cls.supplierName, StringComparison.OrdinalIgnoreCase) || s.AliasesJson.Contains(cls.supplierName, StringComparison.OrdinalIgnoreCase)));
            if (supplier is not null)
            {
                document.SupplierId = supplier.Id;
            }
            else if (cls.supplierName is not null || cls.vatNo is not null)
            {
                // Auto-create supplier from detected info
                supplier = new Supplier
                {
                    Name = cls.supplierName ?? cls.vatNo!,
                    VatNo = cls.vatNo,
                };
                db.Suppliers.Add(supplier);
                await db.SaveChangesAsync(cancellationToken);
                suppliers.Add(supplier);
                document.SupplierId = supplier.Id;
                logger.LogInformation("Auto-created supplier '{Name}' (VAT: {Vat}) for document {DocumentId}",
                    supplier.Name, supplier.VatNo, documentId);
            }
            else
            {
                document.Status = DocumentStatus.NeedsReview;
            }

            // Try supplier-specific extractors first
            string canonical;
            var matchedExtractor = supplierExtractors.FirstOrDefault(e => e.CanHandle(text));
            if (matchedExtractor != null && cls.type != DocumentType.MaterialsList)
            {
                var invoice = matchedExtractor.Extract(pdf, text);
                canonical = JsonSerializer.Serialize(invoice);

                // Populate document header fields from the extracted data
                document.InvoiceNo = invoice.InvoiceNo;
                document.InvoiceDate = invoice.InvoiceDate;
                document.GrossTotal = invoice.GrossTotal;

                logger.LogInformation("Used supplier extractor '{Extractor}' for document {DocumentId}",
                    matchedExtractor.SupplierKey, documentId);

                // Second-pass: use extractor's supplier name to find/create/update supplier
                if (invoice.Supplier is not null)
                {
                    if (document.SupplierId is null)
                    {
                        supplier = suppliers.FirstOrDefault(s =>
                            s.Name.Contains(invoice.Supplier, StringComparison.OrdinalIgnoreCase) ||
                            s.AliasesJson.Contains(invoice.Supplier, StringComparison.OrdinalIgnoreCase));
                        if (supplier is not null)
                        {
                            document.SupplierId = supplier.Id;
                        }
                        else
                        {
                            supplier = new Supplier { Name = invoice.Supplier };
                            db.Suppliers.Add(supplier);
                            await db.SaveChangesAsync(cancellationToken);
                            suppliers.Add(supplier);
                            document.SupplierId = supplier.Id;
                            logger.LogInformation("Auto-created supplier '{Name}' from extractor for document {DocumentId}",
                                supplier.Name, documentId);
                        }
                    }
                    else if (supplier is not null && supplier.Name == supplier.VatNo)
                    {
                        // Supplier was auto-created with VAT as name — update with real name from extractor
                        supplier.Name = invoice.Supplier;
                        logger.LogInformation("Updated supplier name from '{Vat}' to '{Name}' for document {DocumentId}",
                            supplier.VatNo, invoice.Supplier, documentId);
                    }
                }
            }
            else
            {
                // Fallback to generic parser
                var strategy = cls.type switch
                {
                    DocumentType.Invoice when text.Contains("A.0900") => "CodeFirstInvoiceStrategy",
                    DocumentType.Invoice => "GenericInvoiceTableStrategy",
                    DocumentType.MaterialsList => "GenericMaterialsListTableStrategy",
                    _ => "UnknownStrategy"
                };
                canonical = cls.type == DocumentType.MaterialsList
                    ? parser.ParseMaterialsJson(text, strategy)
                    : parser.ParseInvoiceJson(text, strategy);
            }

            document.Status = DocumentStatus.Parsed;
            await SaveArtifact(document.Id, JsonSerializer.Serialize(new { textLength = text.Length, head = text[..Math.Min(500, text.Length)] }), canonical, cancellationToken);

            if (cls.type == DocumentType.Invoice)
            {
                await matchingEngine.MatchInvoiceLinesAsync(document.Id, canonical, cancellationToken);

                var canonicalInvoice = JsonSerializer.Deserialize<CanonicalInvoice>(canonical, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (canonicalInvoice is not null)
                {
                    var validation = invoiceValidator.Validate(canonicalInvoice);
                    if (validation.IsValid && document.SupplierId is not null)
                    {
                        document.Status = DocumentStatus.ReadyToPost;
                    }
                    else
                    {
                        document.Status = DocumentStatus.NeedsReview;
                        if (!validation.IsValid)
                        {
                            db.AuditEvents.Add(new AuditEvent
                            {
                                DocumentId = document.Id,
                                EventType = "VALIDATION_FAIL",
                                Message = validation.Reason ?? "Validare esuata"
                            });
                        }
                    }
                }
                else
                {
                    document.Status = DocumentStatus.ReadyToPost;
                }
            }
            else
            {
                document.Status = DocumentStatus.ReadyToPost;
            }
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Processed document {DocumentId}", documentId);
        }
    }

    private async Task SaveArtifact(Guid documentId, string extractedJson, string canonicalJson, CancellationToken cancellationToken)
    {
        var artifact = await db.ExtractArtifacts.FirstOrDefaultAsync(x => x.DocumentId == documentId, cancellationToken);
        if (artifact is null)
        {
            artifact = new ExtractArtifact { DocumentId = documentId, ExtractedJson = extractedJson, CanonicalJson = canonicalJson };
            db.ExtractArtifacts.Add(artifact);
        }
        else
        {
            artifact.ExtractedJson = extractedJson;
            artifact.CanonicalJson = canonicalJson;
        }

        db.AuditEvents.Add(new AuditEvent { DocumentId = documentId, EventType = "ARTIFACT_SAVED", Message = "Extraction artifacts updated" });
        await db.SaveChangesAsync(cancellationToken);
    }
}
