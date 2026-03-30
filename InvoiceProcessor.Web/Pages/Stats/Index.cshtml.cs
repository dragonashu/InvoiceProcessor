using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Pages.Stats;

public class IndexModel(AppDbContext db) : PageModel
{
    public int TotalDocuments { get; set; }
    public Dictionary<DocumentStatus, int> DocumentsByStatus { get; set; } = new();
    public List<SupplierStat> DocumentsBySupplier { get; set; } = [];
    public int TotalJobs { get; set; }
    public int SuccessJobs { get; set; }
    public int FailedJobs { get; set; }
    public int PartialJobs { get; set; }
    public decimal SuccessRate { get; set; }
    public decimal TotalValueProcessed { get; set; }
    public decimal TotalValueAll { get; set; }

    public record SupplierStat(string Name, int Count, decimal TotalValue);

    public async Task OnGetAsync(CancellationToken ct)
    {
        TotalDocuments = await db.Documents.CountAsync(ct);

        var statusGroups = await db.Documents
            .GroupBy(d => d.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        DocumentsByStatus = statusGroups.ToDictionary(x => x.Status, x => x.Count);

        var docs = await db.Documents.Include(d => d.Supplier).ToListAsync(ct);
        DocumentsBySupplier = docs
            .GroupBy(d => d.Supplier?.DisplayName ?? "Necunoscut")
            .Select(g => new SupplierStat(g.Key, g.Count(), g.Sum(d => d.GrossTotal ?? 0)))
            .OrderByDescending(x => x.TotalValue)
            .ToList();

        TotalJobs = await db.PostingJobs.CountAsync(ct);
        SuccessJobs = await db.PostingJobs.CountAsync(j => j.Status == PostingJobStatus.Success, ct);
        FailedJobs = await db.PostingJobs.CountAsync(j => j.Status == PostingJobStatus.Failed, ct);
        PartialJobs = await db.PostingJobs.CountAsync(j => j.Status == PostingJobStatus.Partial, ct);
        SuccessRate = TotalJobs > 0 ? (decimal)SuccessJobs / TotalJobs : 0;

        TotalValueProcessed = docs.Where(d => d.Status == DocumentStatus.Posted).Sum(d => d.GrossTotal ?? 0);
        TotalValueAll = docs.Sum(d => d.GrossTotal ?? 0);
    }
}
