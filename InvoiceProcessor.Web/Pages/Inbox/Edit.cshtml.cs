using System.Text.Json;
using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using InvoiceProcessor.Web.Services.Extraction;
using InvoiceProcessor.Web.Services.Matching;
using InvoiceProcessor.Web.Services.Robot;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Pages.Inbox;

public class EditModel(AppDbContext db, IInvoiceValidator validator, IMatchingEngine matchingEngine, IPostingJobService postingJobService) : PageModel
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public Document Doc { get; set; } = default!;
    public string? Message { get; set; }
    public Dictionary<int, InvoiceLine> MatchedLines { get; set; } = new();

    [BindProperty] public string? Supplier { get; set; }
    [BindProperty] public string? InvoiceNo { get; set; }
    [BindProperty] public string? InvoiceDate { get; set; }
    [BindProperty] public string? Currency { get; set; }
    [BindProperty] public decimal? NetTotal { get; set; }
    [BindProperty] public decimal? VatTotal { get; set; }
    [BindProperty] public decimal? GrossTotal { get; set; }
    [BindProperty] public bool IsImport { get; set; }
    [BindProperty] public string? HeaderWarehouseCode { get; set; }
    [BindProperty] public List<EditLineInput> Lines { get; set; } = [];

    public class EditLineInput
    {
        public string? CodIntern { get; set; }
        public string DescriptionRaw { get; set; } = "";
        public decimal Qty { get; set; }
        public string? Uom { get; set; }
        public decimal? UnitPrice { get; set; }
        public decimal LineTotal { get; set; }
        public string? Bare { get; set; }
        public string? WarehouseCode { get; set; }
        public string? CostCenterCode { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id, string? message, CancellationToken cancellationToken)
    {
        var doc = await db.Documents.Include(d => d.Supplier)
            .Include(d => d.InvoiceLines).ThenInclude(l => l.MatchedItem)
            .Include(d => d.CustomsDeclaration)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (doc is null) return NotFound();

        if (doc.Status is not (DocumentStatus.NeedsReview or DocumentStatus.Validated or DocumentStatus.ReadyToPost))
            return RedirectToPage("Index", new { message = "Documentul nu poate fi editat in starea curenta." });

        Doc = doc;
        MatchedLines = doc.InvoiceLines.ToDictionary(l => l.LineNo);
        Message = message;
        IsImport = doc.IsImport;
        HeaderWarehouseCode = doc.WarehouseCode;

        var artifact = await db.ExtractArtifacts.FirstOrDefaultAsync(a => a.DocumentId == id, cancellationToken);
        if (artifact is not null)
        {
            var inv = JsonSerializer.Deserialize<CanonicalInvoice>(artifact.CanonicalJson, JsonOpts);
            if (inv is not null)
            {
                Supplier = inv.Supplier;
                InvoiceNo = inv.InvoiceNo;
                InvoiceDate = inv.InvoiceDate?.ToString("yyyy-MM-dd");
                Currency = inv.Currency;
                NetTotal = inv.NetTotal;
                VatTotal = inv.VatTotal;
                GrossTotal = inv.GrossTotal;
                var lineIdx = 0;
                Lines = inv.Lines.Select(l =>
                {
                    lineIdx++;
                    MatchedLines.TryGetValue(lineIdx, out var ml);
                    return new EditLineInput
                    {
                        CodIntern = l.CodIntern,
                        DescriptionRaw = l.DescriptionRaw,
                        Qty = l.Qty,
                        Uom = l.Uom,
                        UnitPrice = l.UnitPrice,
                        LineTotal = l.LineTotal,
                        Bare = l.Bare,
                        WarehouseCode = ml?.WarehouseCode,
                        CostCenterCode = ml?.CostCenterCode
                    };
                }).ToList();
            }
        }

        return Page();
    }

    [BindProperty] public string? Handler { get; set; }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        var doc = await db.Documents.Include(d => d.Supplier).FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (doc is null) return NotFound();

        var artifact = await db.ExtractArtifacts.FirstOrDefaultAsync(a => a.DocumentId == id, cancellationToken);
        if (artifact is null) return RedirectToPage("Index", new { message = "Nu exista date extrase." });

        var existingInvoice = JsonSerializer.Deserialize<CanonicalInvoice>(artifact.CanonicalJson, JsonOpts);

        DateOnly? parsedDate = DateOnly.TryParse(InvoiceDate, out var d) ? d : null;

        var updatedInvoice = new CanonicalInvoice(
            Supplier, InvoiceNo, parsedDate, Currency,
            NetTotal, VatTotal, GrossTotal,
            Lines.Select(l => new CanonicalInvoiceLine(
                l.CodIntern, l.DescriptionRaw, l.Qty, l.Uom, l.UnitPrice, l.LineTotal, l.Bare
            )).ToList(),
            new CanonicalMetadata(
                existingInvoice?.Metadata?.Confidence ?? doc.Confidence,
                existingInvoice?.Metadata?.Strategy ?? "ManualEdit",
                "Editat manual"));

        var canonical = JsonSerializer.Serialize(updatedInvoice);
        artifact.CanonicalJson = canonical;

        doc.InvoiceNo = InvoiceNo;
        doc.InvoiceDate = parsedDate;
        doc.GrossTotal = GrossTotal;
        doc.IsImport = IsImport;
        doc.WarehouseCode = string.IsNullOrWhiteSpace(HeaderWarehouseCode) ? null : HeaderWarehouseCode;

        // Re-run matching
        await matchingEngine.MatchInvoiceLinesAsync(doc.Id, canonical, cancellationToken);

        // Apply warehouse and cost center codes from form to invoice lines
        var savedLines = await db.InvoiceLines.Where(l => l.DocumentId == doc.Id).OrderBy(l => l.LineNo).ToListAsync(cancellationToken);
        for (int i = 0; i < savedLines.Count && i < Lines.Count; i++)
        {
            savedLines[i].WarehouseCode = Lines[i].WarehouseCode;
            savedLines[i].CostCenterCode = Lines[i].CostCenterCode;
        }

        var validation = validator.Validate(updatedInvoice);

        db.AuditEvents.Add(new AuditEvent
        {
            DocumentId = doc.Id,
            EventType = "MANUAL_EDIT",
            Message = $"Editat manual. Validare: {(validation.IsValid ? "OK" : validation.Reason)}"
        });

        // Send to robot if requested (regardless of validation)
        if (Handler == "SendToRobot")
        {
            doc.Status = DocumentStatus.ReadyToPost;
            await db.SaveChangesAsync(cancellationToken);

            var jobs = await postingJobService.CreatePostingJobsAsync([doc.Id], cancellationToken);
            return RedirectToPage("Edit", new { id, message = $"Factura trimisa la robot ({jobs.Count} job creat)." });
        }

        // Save only — keep the current status unchanged
        await db.SaveChangesAsync(cancellationToken);

        var msg = validation.IsValid
            ? "Salvat cu succes."
            : $"Salvat. {validation.Reason}";
        return RedirectToPage("Edit", new { id, message = msg });
    }
}
