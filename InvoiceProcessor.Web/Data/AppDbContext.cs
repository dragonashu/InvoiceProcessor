using InvoiceProcessor.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace InvoiceProcessor.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<ExtractArtifact> ExtractArtifacts => Set<ExtractArtifact>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<PostingJob> PostingJobs => Set<PostingJob>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SupplierItemMapping> SupplierItemMappings => Set<SupplierItemMapping>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<CostCenter> CostCenters => Set<CostCenter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Document>().HasIndex(x => x.PdfHash).IsUnique();
        modelBuilder.Entity<Document>().HasIndex(x => new { x.SupplierId, x.InvoiceNo, x.InvoiceDate, x.GrossTotal });
        modelBuilder.Entity<ExtractArtifact>().HasIndex(x => x.DocumentId).IsUnique();
        modelBuilder.Entity<PostingJob>().HasIndex(x => new { x.DocumentId, x.Status });
        modelBuilder.Entity<CatalogItem>().HasIndex(x => x.ErpItemCode).IsUnique();
        modelBuilder.Entity<CatalogItem>().HasIndex(x => x.InternalCode);
        modelBuilder.Entity<Warehouse>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<CostCenter>().HasIndex(x => x.Code).IsUnique();

        modelBuilder.Entity<Document>()
            .HasOne(d => d.ExtractArtifact)
            .WithOne(a => a.Document)
            .HasForeignKey<ExtractArtifact>(a => a.DocumentId);

        modelBuilder.Entity<InvoiceLine>()
            .HasOne(l => l.MatchedItem)
            .WithMany()
            .HasForeignKey(l => l.MatchedItemId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<SupplierItemMapping>()
            .HasOne(m => m.Supplier)
            .WithMany()
            .HasForeignKey(m => m.SupplierId);

        modelBuilder.Entity<SupplierItemMapping>()
            .HasOne(m => m.CatalogItem)
            .WithMany()
            .HasForeignKey(m => m.CatalogItemId);
    }
}
