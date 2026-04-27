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
    public List<PostingJob> ActiveJobs { get; set; } = [];
    public List<PostingJob> FinalizedJobs { get; set; } = [];
    public string? Message { get; set; }

    public async Task OnGetAsync(string? message, CancellationToken cancellationToken)
    {
        Message = message;
        await LoadJobsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostTestRpaAsync(CancellationToken cancellationToken)
    {
        var jobs = await db.PostingJobs
            .Include(j => j.Document)
            .Where(j => j.Status == PostingJobStatus.Queued || j.Status == PostingJobStatus.Claimed)
            .OrderBy(j => j.CreatedAt)
            .Take(3)
            .ToListAsync(cancellationToken);

        if (jobs.Count == 0)
            return RedirectToPage(new { message = "No queued jobs to simulate." });

        var completed = 0;
        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            job.ClaimedAt = DateTime.UtcNow;
            job.CompletedAt = DateTime.UtcNow;

            if (i < 2)
            {
                job.Status = PostingJobStatus.Success;
                job.ErpDocNo = $"WM/2026/{(1000 + i):D6}";
                job.Document.Status = DocumentStatus.Posted;
            }
            else
            {
                job.Status = PostingJobStatus.Partial;
                job.ErrorCategory = "MANUAL";
                job.ErrorMessage = "Requires manual review - line items mismatch";
                job.Document.Status = DocumentStatus.NeedsReview;
            }

            completed++;
        }

        await db.SaveChangesAsync(cancellationToken);
        var successCount = Math.Min(2, completed);
        var manualCount = completed > 2 ? 1 : 0;
        return RedirectToPage(new { message = $"Test RPA: {completed} job(s) simulated ({successCount} success, {manualCount} manual)." });
    }

    public async Task<IActionResult> OnPostRemoveJobsAsync(CancellationToken cancellationToken)
    {
        var jobs = await db.PostingJobs
            .Include(j => j.Document)
            .ToListAsync(cancellationToken);

        foreach (var job in jobs)
        {
            // Reset documents back to Validated so they can be resubmitted
            if (job.Document.Status is DocumentStatus.Posting or DocumentStatus.Posted or DocumentStatus.Failed)
                job.Document.Status = DocumentStatus.Validated;
        }

        db.PostingJobs.RemoveRange(jobs);
        await db.SaveChangesAsync(cancellationToken);

        return RedirectToPage(new { message = $"{jobs.Count} job(s) removed." });
    }

    private async Task LoadJobsAsync(CancellationToken cancellationToken)
    {
        var query = db.PostingJobs.Include(j => j.Document).ThenInclude(d => d.Supplier).AsQueryable();
        if (Status.HasValue) query = query.Where(j => j.Status == Status);
        if (SupplierId.HasValue) query = query.Where(j => j.Document.SupplierId == SupplierId);

        Jobs = await query.OrderByDescending(j => j.CreatedAt).Take(500).ToListAsync(cancellationToken);

        ActiveJobs = Jobs.Where(j => j.Status is not (PostingJobStatus.Success or PostingJobStatus.Partial)).ToList();
        FinalizedJobs = Jobs.Where(j => j.Status is PostingJobStatus.Success or PostingJobStatus.Partial).ToList();
    }
}
