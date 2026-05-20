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
    public DbSet<CatalogJob> CatalogJobs => Set<CatalogJob>();
    public DbSet<ItemClass> ItemClasses => Set<ItemClass>();
    public DbSet<CustomsDeclaration> CustomsDeclarations => Set<CustomsDeclaration>();
    public DbSet<CatalogImportLog> CatalogImportLogs => Set<CatalogImportLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Document>().HasIndex(x => x.PdfHash).IsUnique();
        modelBuilder.Entity<Document>().HasIndex(x => new { x.SupplierId, x.InvoiceNo, x.InvoiceDate, x.GrossTotal });
        modelBuilder.Entity<ExtractArtifact>().HasIndex(x => x.DocumentId).IsUnique();
        modelBuilder.Entity<PostingJob>().HasIndex(x => new { x.DocumentId, x.Status });
        // Filtered unique — ErpItemCode "default" is a placeholder for Yildiz proposals
        // (ERP generates the real code), so multiple rows may share it.
        modelBuilder.Entity<CatalogItem>()
            .HasIndex(x => x.ErpItemCode)
            .IsUnique()
            .HasFilter("\"ErpItemCode\" <> 'default'");
        modelBuilder.Entity<CatalogJob>().HasIndex(x => new { x.CatalogItemId, x.Status });
        modelBuilder.Entity<CatalogJob>()
            .HasOne(j => j.CatalogItem)
            .WithMany()
            .HasForeignKey(j => j.CatalogItemId);

        modelBuilder.Entity<Warehouse>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<CostCenter>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<ItemClass>().HasIndex(x => x.Name).IsUnique();

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
