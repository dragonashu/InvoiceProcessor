using System.Text.Json;
using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Pages.Inbox;

public class PreviewModel(AppDbContext db) : PageModel
{
    public Document Doc { get; set; } = default!;
    public CanonicalInvoice? Invoice { get; set; }
    public string? RawCanonicalJson { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var doc = await db.Documents
            .Include(d => d.Supplier)
            .Include(d => d.InvoiceLines)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

        if (doc is null) return NotFound();
        Doc = doc;

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
        }

        return Page();
    }
}
