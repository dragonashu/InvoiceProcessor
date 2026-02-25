using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using InvoiceProcessor.Web.Services.Robot;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Pages.Inbox;

public class IndexModel(AppDbContext db, IPostingJobService postingJobService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid? SupplierId { get; set; }

    [BindProperty]
    public List<Guid> SelectedDocumentIds { get; set; } = [];

    public List<SupplierSummaryVm> Suppliers { get; set; } = [];
    public List<Document> Documents { get; set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Suppliers = await db.Suppliers
            .Select(s => new SupplierSummaryVm(s.Id, s.Name, db.Documents.Count(d => d.SupplierId == s.Id && d.Status == DocumentStatus.Received), db.Documents.Count(d => d.SupplierId == s.Id && d.Status == DocumentStatus.ReadyToPost), db.Documents.Count(d => d.SupplierId == s.Id && d.Status == DocumentStatus.NeedsReview)))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var query = db.Documents.Include(d => d.Supplier).OrderByDescending(d => d.CreatedAt).AsQueryable();
        if (SupplierId.HasValue) query = query.Where(d => d.SupplierId == SupplierId);

        Documents = await query.Take(300).ToListAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSendJobAsync(CancellationToken cancellationToken)
    {
        await postingJobService.CreatePostingJobsAsync(SelectedDocumentIds, cancellationToken);
        return RedirectToPage(new { SupplierId });
    }

    public record SupplierSummaryVm(Guid Id, string Name, int ReceivedCount, int ReadyCount, int ReviewCount);
}
