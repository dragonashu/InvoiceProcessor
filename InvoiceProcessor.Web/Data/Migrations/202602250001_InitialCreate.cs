using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceProcessor.Web.Data.Migrations;

public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CatalogItems",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ErpItemCode = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Uom = table.Column<string>(type: "TEXT", nullable: true),
                TaxCode = table.Column<string>(type: "TEXT", nullable: true),
                Active = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_CatalogItems", x => x.Id));

        migrationBuilder.CreateTable(
            name: "Suppliers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                VatNo = table.Column<string>(type: "TEXT", nullable: true),
                Country = table.Column<string>(type: "TEXT", nullable: true),
                AliasesJson = table.Column<string>(type: "TEXT", nullable: false),
                Active = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Suppliers", x => x.Id));

        migrationBuilder.CreateTable(
            name: "Documents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                EmailFrom = table.Column<string>(type: "TEXT", nullable: true),
                EmailSubject = table.Column<string>(type: "TEXT", nullable: true),
                Filename = table.Column<string>(type: "TEXT", nullable: false),
                PdfHash = table.Column<string>(type: "TEXT", nullable: false),
                StoragePath = table.Column<string>(type: "TEXT", nullable: false),
                DocType = table.Column<int>(type: "INTEGER", nullable: false),
                SupplierId = table.Column<Guid>(type: "TEXT", nullable: true),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                Confidence = table.Column<decimal>(type: "TEXT", nullable: false),
                InvoiceNo = table.Column<string>(type: "TEXT", nullable: true),
                InvoiceDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                GrossTotal = table.Column<decimal>(type: "TEXT", nullable: true),
                CorrelationId = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Documents", x => x.Id);
                table.ForeignKey("FK_Documents_Suppliers_SupplierId", x => x.SupplierId, "Suppliers", "Id");
            });

        migrationBuilder.CreateTable(
            name: "SupplierItemMappings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                SupplierId = table.Column<Guid>(type: "TEXT", nullable: false),
                VendorCode = table.Column<string>(type: "TEXT", nullable: true),
                Pattern = table.Column<string>(type: "TEXT", nullable: true),
                CatalogItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                Active = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SupplierItemMappings", x => x.Id);
                table.ForeignKey("FK_SIM_CatalogItems", x => x.CatalogItemId, "CatalogItems", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_SIM_Suppliers", x => x.SupplierId, "Suppliers", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AuditEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentId = table.Column<Guid>(type: "TEXT", nullable: true),
                EventType = table.Column<string>(type: "TEXT", nullable: false),
                Message = table.Column<string>(type: "TEXT", nullable: false),
                PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditEvents", x => x.Id);
                table.ForeignKey("FK_AuditEvents_Documents_DocumentId", x => x.DocumentId, "Documents", "Id");
            });

        migrationBuilder.CreateTable(
            name: "ExtractArtifacts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                ExtractedJson = table.Column<string>(type: "TEXT", nullable: false),
                CanonicalJson = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ExtractArtifacts", x => x.Id);
                table.ForeignKey("FK_ExtractArtifacts_Documents_DocumentId", x => x.DocumentId, "Documents", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "InvoiceLines",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                LineNo = table.Column<int>(type: "INTEGER", nullable: false),
                VendorCode = table.Column<string>(type: "TEXT", nullable: true),
                Description = table.Column<string>(type: "TEXT", nullable: false),
                Qty = table.Column<decimal>(type: "TEXT", nullable: false),
                Uom = table.Column<string>(type: "TEXT", nullable: true),
                UnitPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                MatchedItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                MatchConfidence = table.Column<decimal>(type: "TEXT", nullable: false),
                MatchReason = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InvoiceLines", x => x.Id);
                table.ForeignKey("FK_InvoiceLines_CatalogItems_MatchedItemId", x => x.MatchedItemId, "CatalogItems", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_InvoiceLines_Documents_DocumentId", x => x.DocumentId, "Documents", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PostingJobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                BatchId = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ClaimedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                RequestJson = table.Column<string>(type: "TEXT", nullable: false),
                ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                ErpDocNo = table.Column<string>(type: "TEXT", nullable: true),
                ErrorCategory = table.Column<string>(type: "TEXT", nullable: true),
                ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PostingJobs", x => x.Id);
                table.ForeignKey("FK_PostingJobs_Documents_DocumentId", x => x.DocumentId, "Documents", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(name: "IX_Documents_PdfHash", table: "Documents", column: "PdfHash", unique: true);
        migrationBuilder.CreateIndex(name: "IX_Documents_SupplierId_InvoiceNo_InvoiceDate_GrossTotal", table: "Documents", columns: new[] { "SupplierId", "InvoiceNo", "InvoiceDate", "GrossTotal" });
        migrationBuilder.CreateIndex(name: "IX_ExtractArtifacts_DocumentId", table: "ExtractArtifacts", column: "DocumentId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_InvoiceLines_DocumentId", table: "InvoiceLines", column: "DocumentId");
        migrationBuilder.CreateIndex(name: "IX_InvoiceLines_MatchedItemId", table: "InvoiceLines", column: "MatchedItemId");
        migrationBuilder.CreateIndex(name: "IX_PostingJobs_DocumentId_Status", table: "PostingJobs", columns: new[] { "DocumentId", "Status" });
        migrationBuilder.CreateIndex(name: "IX_AuditEvents_DocumentId", table: "AuditEvents", column: "DocumentId");
        migrationBuilder.CreateIndex(name: "IX_SupplierItemMappings_CatalogItemId", table: "SupplierItemMappings", column: "CatalogItemId");
        migrationBuilder.CreateIndex(name: "IX_SupplierItemMappings_SupplierId", table: "SupplierItemMappings", column: "SupplierId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("AuditEvents");
        migrationBuilder.DropTable("ExtractArtifacts");
        migrationBuilder.DropTable("InvoiceLines");
        migrationBuilder.DropTable("PostingJobs");
        migrationBuilder.DropTable("SupplierItemMappings");
        migrationBuilder.DropTable("Documents");
        migrationBuilder.DropTable("CatalogItems");
        migrationBuilder.DropTable("Suppliers");
    }
}
