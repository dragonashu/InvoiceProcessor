using System.Text.Json;
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

        string text;
        try
        {
            using var pdf = PdfDocument.Open(document.StoragePath);
            text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        }
        catch (Exception ex)
        {
            document.Status = DocumentStatus.Failed;
            db.AuditEvents.Add(new AuditEvent { DocumentId = document.Id, EventType = "EXTRACT_FAIL", Message = ex.Message });
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

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
        else
        {
            document.Status = DocumentStatus.NeedsReview;
        }

        var strategy = cls.type switch
        {
            DocumentType.Invoice when text.Contains("A.0900") => "CodeFirstInvoiceStrategy",
            DocumentType.Invoice => "GenericInvoiceTableStrategy",
            DocumentType.MaterialsList => "GenericMaterialsListTableStrategy",
            _ => "UnknownStrategy"
        };

        var canonical = cls.type == DocumentType.MaterialsList ? parser.ParseMaterialsJson(text, strategy) : parser.ParseInvoiceJson(text, strategy);
        document.Status = DocumentStatus.Parsed;

        await SaveArtifact(document.Id, JsonSerializer.Serialize(new { textLength = text.Length, head = text[..Math.Min(500, text.Length)] }), canonical, cancellationToken);

        if (cls.type == DocumentType.Invoice)
        {
            await matchingEngine.MatchInvoiceLinesAsync(document.Id, canonical, cancellationToken);
        }

        document.Status = DocumentStatus.Validated;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Processed document {DocumentId}", documentId);
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
