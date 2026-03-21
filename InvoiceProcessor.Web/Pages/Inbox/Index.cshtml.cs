using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using InvoiceProcessor.Web.Services.Email;
using InvoiceProcessor.Web.Services.Extraction;
using InvoiceProcessor.Web.Services.Robot;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Pages.Inbox;

public class IndexModel(AppDbContext db, IPostingJobService postingJobService, IEmailDispatcher emailDispatcher, IExtractionPipeline extractionPipeline) : PageModel
{
    [BindProperty]
    public List<Guid> SelectedDocumentIds { get; set; } = [];

    public List<SupplierGroup> SupplierGroups { get; set; } = [];
    public string? Message { get; set; }

    private static readonly DocumentStatus[] ActionableStatuses =
    [
        DocumentStatus.Received, DocumentStatus.Extracting, DocumentStatus.Extracted,
        DocumentStatus.Classified, DocumentStatus.Parsed, DocumentStatus.Matched,
        DocumentStatus.Validated, DocumentStatus.ReadyToPost, DocumentStatus.NeedsReview
    ];

    private static readonly DocumentStatus[] SelectableStatuses =
    [
        DocumentStatus.ReadyToPost
    ];

    public async Task OnGetAsync(string? message, CancellationToken cancellationToken)
    {
        Message = message;

        // Load all documents in actionable statuses, grouped by supplier
        var documents = await db.Documents
            .Include(d => d.Supplier)
            .Where(d => ActionableStatuses.Contains(d.Status))
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

        // Group by supplier (null = unknown)
        var groups = documents
            .GroupBy(d => d.SupplierId)
            .Select(g =>
            {
                var supplier = g.First().Supplier;
                return new SupplierGroup
                {
                    SupplierId = g.Key,
                    SupplierName = supplier?.Name ?? "Unknown Supplier",
                    SupplierVat = supplier?.VatNo,
                    Documents = g.ToList(),
                    TotalCount = g.Count(),
                    ReadyCount = g.Count(d => d.Status == DocumentStatus.ReadyToPost),
                    ReviewCount = g.Count(d => d.Status == DocumentStatus.NeedsReview),
                    ProcessingCount = g.Count(d => d.Status < DocumentStatus.ReadyToPost && d.Status != DocumentStatus.NeedsReview)
                };
            })
            .OrderBy(g => g.SupplierName)
            .ToList();

        SupplierGroups = groups;
    }

    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken cancellationToken)
    {
        var ingested = await emailDispatcher.PollAsync(cancellationToken);
        await extractionPipeline.ProcessPendingAsync(cancellationToken);
        return RedirectToPage(new { message = $"{ingested} fisier(e) noi importate si procesate." });
    }

    public async Task<IActionResult> OnPostSendJobAsync(CancellationToken cancellationToken)
    {
        if (SelectedDocumentIds.Count == 0)
            return RedirectToPage(new { message = "No invoices selected." });

        var jobs = await postingJobService.CreatePostingJobsAsync(SelectedDocumentIds, cancellationToken);
        return RedirectToPage(new { message = $"{jobs.Count} job(s) created successfully." });
    }

    public static bool IsSelectable(Document doc) =>
        doc.DocType == DocumentType.Invoice && SelectableStatuses.Contains(doc.Status);

    public static string StatusCssClass(DocumentStatus status) => status switch
    {
        DocumentStatus.ReadyToPost => "status-ready",
        DocumentStatus.NeedsReview => "status-review",
        DocumentStatus.Posted => "status-posted",
        DocumentStatus.Failed => "status-failed",
        DocumentStatus.NeedsOcr => "status-failed",
        _ => "status-processing"
    };

    public static string StatusDisplayName(DocumentStatus status) => status switch
    {
        DocumentStatus.Received => "Primit",
        DocumentStatus.Extracting => "Se extrage",
        DocumentStatus.Extracted => "Extras",
        DocumentStatus.Classified => "Clasificat",
        DocumentStatus.Parsed => "Procesat",
        DocumentStatus.Matched => "Asociat",
        DocumentStatus.Validated => "Validat",
        DocumentStatus.ReadyToPost => "Gata de postare",
        DocumentStatus.Posting => "Se posteaza",
        DocumentStatus.Posted => "Postat",
        DocumentStatus.NeedsReview => "Necesita verificare",
        DocumentStatus.NeedsOcr => "Necesita OCR",
        DocumentStatus.Failed => "Eroare",
        DocumentStatus.Duplicate => "Duplicat",
        _ => status.ToString()
    };

    public class SupplierGroup
    {
        public Guid? SupplierId { get; set; }
        public string SupplierName { get; set; } = "";
        public string? SupplierVat { get; set; }
        public List<Document> Documents { get; set; } = [];
        public int TotalCount { get; set; }
        public int ReadyCount { get; set; }
        public int ReviewCount { get; set; }
        public int ProcessingCount { get; set; }
    }
}
