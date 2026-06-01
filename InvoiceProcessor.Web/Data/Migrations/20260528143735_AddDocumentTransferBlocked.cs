using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceProcessor.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentTransferBlocked : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TransferBlocked",
                table: "Documents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TransferBlocked",
                table: "Documents");
        }
    }
}
