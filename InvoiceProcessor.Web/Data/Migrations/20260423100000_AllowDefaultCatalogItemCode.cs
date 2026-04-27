using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceProcessor.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowDefaultCatalogItemCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CatalogItems_ErpItemCode",
                table: "CatalogItems");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogItems_ErpItemCode",
                table: "CatalogItems",
                column: "ErpItemCode",
                unique: true,
                filter: "\"ErpItemCode\" <> 'default'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CatalogItems_ErpItemCode",
                table: "CatalogItems");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogItems_ErpItemCode",
                table: "CatalogItems",
                column: "ErpItemCode",
                unique: true);
        }
    }
}
