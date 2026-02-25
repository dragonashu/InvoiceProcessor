using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Pages.Status;

public class IndexModel(AppDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public PostingJobStatus? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? SupplierId { get; set; }

    public List<PostingJob> Jobs { get; set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var query = db.PostingJobs.Include(j => j.Document).ThenInclude(d => d.Supplier).AsQueryable();
        if (Status.HasValue) query = query.Where(j => j.Status == Status);
        if (SupplierId.HasValue) query = query.Where(j => j.Document.SupplierId == SupplierId);

        Jobs = await query.OrderByDescending(j => j.CreatedAt).Take(500).ToListAsync(cancellationToken);
    }
}
