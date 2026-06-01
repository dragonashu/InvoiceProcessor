using System.Text.Json;
using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using InvoiceProcessor.Web.Services.Extraction;
using InvoiceProcessor.Web.Services.Robot;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Pages.Inbox;

public class PreviewModel(AppDbContext db, IPostingJobService postingJobService) : PageModel
{
    public Document Doc { get; set; } = default!;
    public CanonicalInvoice? Invoice { get; set; }
    public string? RawCanonicalJson { get; set; }
    public Dictionary<int, InvoiceLine> MatchedLines { get; set; } = new();
    public int PendingNewItems { get; set; }
    public ExtractionCheckResult? Checks { get; set; }
    public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, string? message, CancellationToken cancellationToken)
    {
        Message = message;
        var doc = await db.Documents
            .Include(d => d.Supplier)
            .Include(d => d.InvoiceLines).ThenInclude(l => l.MatchedItem)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

        if (doc is null) return NotFound();
        Doc = doc;
        MatchedLines = doc.InvoiceLines.ToDictionary(l => l.LineNo);
        PendingNewItems = IndexModel.GetPendingItemsCount(doc);

        var artifact = await db.ExtractArtifacts
            .FirstOrDefaultAsync(a => a.DocumentId == id, cancellationToken);

        if (artifact is not null)
        {
            RawCanonicalJson = artifact.CanonicalJson;
            try
            {
                Invoice = JsonSerializer.Deserialize<CanonicalInvoice>(artifact.CanonicalJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { /* leave Invoice null if parse fails */ }

            if (Invoice is not null)
                Checks = ExtractionChecks.Evaluate(Invoice);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSendToRobotAsync(Guid id, CancellationToken cancellationToken)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (doc is null) return NotFound();

        // Guard the server side too: a blocked document must never create a posting job,
        // even if the disabled button is bypassed.
        if (doc.TransferBlocked)
            return RedirectToPage(new { id, message = "Transfer blocat: verificarile de extractie au esuat." });

        doc.Status = DocumentStatus.ReadyToPost;
        await db.SaveChangesAsync(cancellationToken);

        var jobs = await postingJobService.CreatePostingJobsAsync([id], cancellationToken);
        return RedirectToPage(new { id, message = $"{jobs.Count} job creat." });
    }
}
