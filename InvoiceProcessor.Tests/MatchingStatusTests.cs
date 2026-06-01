using System.Text.Json;
using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Data;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Models;
using InvoiceProcessor.Web.Services.Matching;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvoiceProcessor.Tests;

public class MatchingStatusTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<AppDbContext> _options;

    public MatchingStatusTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
    }
    public void Dispose() => _conn.Dispose();
    private AppDbContext NewDb() => new(_options);

    // A line whose code isn't in the catalog is auto-created (a "new item"). Those are
    // reviewed in the send-time modal, so they must NOT hold the invoice in NeedsReview —
    // the document should be ReadyToPost (and therefore selectable in the Inbox).
    [Fact]
    public async Task NewItemsDoNotForceNeedsReview()
    {
        await using var db = NewDb();
        var supplier = new Supplier { Name = "Aliplast", VatNo = "PL9462354607" };
        db.Suppliers.Add(supplier);
        var doc = new Document { Filename = "x.pdf", PdfHash = Guid.NewGuid().ToString("N"), DocType = DocumentType.Invoice, SupplierId = supplier.Id, GrossTotal = 10m };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        // Empty catalog → the line cannot match → auto-created.
        var canonical = new CanonicalInvoice(
            "Aliplast Sp. z o.o.", "1", null, "EUR", 10m, 0m, 10m,
            [new CanonicalInvoiceLine("NEWCODE1", "some brand new accessory", 1, "BUC", 10m, 10m)],
            new CanonicalMetadata(0.85m, "Test"), ExpectedLineCount: 1);

        var engine = new MatchingEngine(db, NullLogger<MatchingEngine>.Instance);
        await engine.MatchInvoiceLinesAsync(doc.Id, JsonSerializer.Serialize(canonical), CancellationToken.None);

        var updated = await db.Documents.FirstAsync(d => d.Id == doc.Id);
        var line = await db.InvoiceLines.FirstAsync(l => l.DocumentId == doc.Id);

        Assert.Equal("auto-created", line.MatchReason);
        Assert.True(line.MatchConfidence < 0.75m);            // still a low (new-item) score
        Assert.Equal(DocumentStatus.ReadyToPost, updated.Status); // but the invoice is NOT parked for review
        Assert.False(updated.TransferBlocked);                 // totals + line count balance
    }
}
