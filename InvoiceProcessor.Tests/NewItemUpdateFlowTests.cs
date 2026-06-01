using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Controllers;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using InvoiceProcessor.Web.Services.Robot;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceProcessor.Tests;

// Reproduces the new-items modal flow: a proposed (auto-created) catalog item is edited
// in the popup shown after "Trimite la robot", accepted, and then claimed by the robot
// from the catalog API. The edited values must reach the catalog API payload.
public class NewItemUpdateFlowTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<AppDbContext> _options;

    public NewItemUpdateFlowTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private AppDbContext NewDb() => new(_options);

    // No-op posting service: the catalog endpoints don't use it, but the controllers require it.
    private sealed class FakePostingJobService : IPostingJobService
    {
        public Task<IReadOnlyList<PostingJob>> CreatePostingJobsAsync(IReadOnlyList<Guid> documentIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PostingJob>>([]);
        public Task<IReadOnlyList<PostingJob>> ListJobsAsync(PostingJobStatus? status, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PostingJob>>([]);
        public Task<PostingJob?> GetJobAsync(Guid jobId, CancellationToken ct) => Task.FromResult<PostingJob?>(null);
        public Task<ReadyToPostInvoicePayload?> ClaimNextJobAsync(CancellationToken ct) => Task.FromResult<ReadyToPostInvoicePayload?>(null);
        public Task<PostingJob> UpdateJobAsync(Guid jobId, RobotUpdateRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task CompleteJobAsync(Guid jobId, RobotCompleteRequest request, CancellationToken ct) => Task.CompletedTask;
    }

    private async Task<(Guid itemId, Guid docId)> SeedProposedItemAsync()
    {
        await using var db = NewDb();
        var doc = new Document { Filename = "inv.pdf", PdfHash = Guid.NewGuid().ToString("N"), DocType = DocumentType.Invoice };
        var item = new CatalogItem
        {
            ErpItemCode = "ACFX531-500/IN",
            Name = "OLD NAME",
            Uom = "BUC",
            IsAutoCreated = true,
            AutoCreatedAt = DateTime.UtcNow,
            AcceptedAt = null,
            Active = true
        };
        db.Documents.Add(doc);
        db.CatalogItems.Add(item);
        db.InvoiceLines.Add(new InvoiceLine
        {
            DocumentId = doc.Id,
            LineNo = 1,
            VendorCode = "ACFX531-500/IN",
            Description = "F530 HANDLE ONE SIDE",
            Qty = 3,
            Uom = "BUC",
            Amount = 50.58m,
            MatchedItemId = item.Id,
            MatchReason = "auto-created",
            MatchConfidence = 0.5m,
            ExternalCode = "OLDEXT",
            PropertyClass = "ACCESORII DE SISTEM"
        });
        await db.SaveChangesAsync();
        return (item.Id, doc.Id);
    }

    [Fact]
    public async Task EditedValues_ReachCatalogApiPayload()
    {
        var (itemId, _) = await SeedProposedItemAsync();

        // 1) User edits every field in the popup and accepts.
        var edited = new AcceptNewItemRequest(
            ErpItemCode: "ACFX531-500/IN-NEW",
            Name: "NEW NAME EDITED",
            Uom: "SET",
            ExternalCode: "NEWEXT",
            PropertyClass: "PROFILE DE SISTEM AL");

        await using (var db = NewDb())
        {
            var ui = new UiController(db, new FakePostingJobService());
            var result = await ui.AcceptNewItem(itemId, edited, CancellationToken.None);
            Assert.IsType<OkObjectResult>(result);
        }

        // 2) Catalog item itself is persisted with the edited code/name/uom.
        await using (var db = NewDb())
        {
            var saved = await db.CatalogItems.FirstAsync(c => c.Id == itemId);
            Assert.Equal("ACFX531-500/IN-NEW", saved.ErpItemCode);
            Assert.Equal("NEW NAME EDITED", saved.Name);
            Assert.Equal("SET", saved.Uom);
            Assert.NotNull(saved.AcceptedAt);
        }

        // 3) The robot claims the catalog job from the API — the payload must carry every edit.
        await using (var db = NewDb())
        {
            var robot = new RobotController(new FakePostingJobService(), db);
            var next = await robot.NextCatalogJob(CancellationToken.None);
            var payload = Assert.IsType<CatalogItemPayload>(Assert.IsType<OkObjectResult>(next).Value);

            Assert.Equal("ACFX531-500/IN-NEW", payload.Code);
            Assert.Equal("NEW NAME EDITED", payload.Name);
            Assert.Equal("SET", payload.Uom);
            Assert.Equal("NEWEXT", payload.ExternalCode);
            Assert.Equal("PROFILE DE SISTEM AL", payload.PropertyClass);
        }

        // 4) The matched invoice line must also reflect the edits, so the invoice posting
        //    payload (which reads ExternalCode/PropertyClass from the line) is not stale.
        await using (var db = NewDb())
        {
            var line = await db.InvoiceLines.FirstAsync(l => l.MatchedItemId == itemId);
            Assert.Equal("NEWEXT", line.ExternalCode);
            Assert.Equal("PROFILE DE SISTEM AL", line.PropertyClass);
        }
    }

    // Accepting many distinct new items must produce one catalog job per item, and the
    // list API must return them all (regression for "approved 56, only a couple visible").
    [Fact]
    public async Task BulkAccept_CreatesOneCatalogJobPerItem_AllVisibleInListApi()
    {
        const int count = 56;
        var ids = new List<Guid>();
        await using (var db = NewDb())
        {
            var doc = new Document { Filename = "inv.pdf", PdfHash = Guid.NewGuid().ToString("N"), DocType = DocumentType.Invoice };
            db.Documents.Add(doc);
            for (var i = 0; i < count; i++)
            {
                var item = new CatalogItem { ErpItemCode = $"CODE{i:000}", Name = $"Item {i}", Uom = "BUC", IsAutoCreated = true, AutoCreatedAt = DateTime.UtcNow, Active = true };
                db.CatalogItems.Add(item);
                db.InvoiceLines.Add(new InvoiceLine { DocumentId = doc.Id, LineNo = i + 1, VendorCode = item.ErpItemCode, Description = $"Item {i}", Qty = 1, Uom = "BUC", Amount = 1m, MatchedItemId = item.Id, MatchReason = "auto-created", MatchConfidence = 0.5m });
                ids.Add(item.Id);
            }
            await db.SaveChangesAsync();
        }

        var ok = 0;
        foreach (var id in ids)
        {
            await using var db = NewDb();
            var item = await db.CatalogItems.FirstAsync(c => c.Id == id);
            var ui = new UiController(db, new FakePostingJobService());
            if (await ui.AcceptNewItem(id, new AcceptNewItemRequest(item.ErpItemCode, item.Name, item.Uom, null, null), CancellationToken.None) is OkObjectResult)
                ok++;
        }
        Assert.Equal(count, ok);

        await using (var db = NewDb())
        {
            Assert.Equal(count, await db.CatalogJobs.CountAsync());
            var robot = new RobotController(new FakePostingJobService(), db);
            var listed = (await robot.ListCatalogJobs(null, 500, CancellationToken.None) as OkObjectResult)?.Value as System.Collections.IEnumerable;
            Assert.Equal(count, listed!.Cast<object>().Count());
        }
    }

    // When the popup leaves external code / class untouched (empty), the values must
    // fall back to the source invoice line rather than being lost.
    [Fact]
    public async Task EmptyExternalAndClass_FallBackToInvoiceLine()
    {
        var (itemId, _) = await SeedProposedItemAsync();

        var minimal = new AcceptNewItemRequest(
            ErpItemCode: "ACFX531-500/IN",
            Name: "KEEP NAME",
            Uom: "BUC",
            ExternalCode: null,
            PropertyClass: null);

        await using (var db = NewDb())
        {
            var ui = new UiController(db, new FakePostingJobService());
            Assert.IsType<OkObjectResult>(await ui.AcceptNewItem(itemId, minimal, CancellationToken.None));
        }

        await using (var db = NewDb())
        {
            var robot = new RobotController(new FakePostingJobService(), db);
            var next = await robot.NextCatalogJob(CancellationToken.None);
            var payload = Assert.IsType<CatalogItemPayload>(Assert.IsType<OkObjectResult>(next).Value);
            Assert.Equal("OLDEXT", payload.ExternalCode);
            Assert.Equal("ACCESORII DE SISTEM", payload.PropertyClass);
        }
    }
}
