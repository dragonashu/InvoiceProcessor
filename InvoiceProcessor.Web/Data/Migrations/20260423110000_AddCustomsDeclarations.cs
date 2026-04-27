using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceProcessor.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomsDeclarations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomsDeclarations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Filename = table.Column<string>(type: "TEXT", nullable: false),
                    StoragePath = table.Column<string>(type: "TEXT", nullable: false),
                    Mrn = table.Column<string>(type: "TEXT", nullable: true),
                    Lrn = table.Column<string>(type: "TEXT", nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "TEXT", nullable: true),
                    InvoiceRef = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomsDeclarations", x => x.Id);
                });

            migrationBuilder.AddColumn<Guid>(
                name: "CustomsDeclarationId",
                table: "Documents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_CustomsDeclarationId",
                table: "Documents",
                column: "CustomsDeclarationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_CustomsDeclarations_CustomsDeclarationId",
                table: "Documents",
                column: "CustomsDeclarationId",
                principalTable: "CustomsDeclarations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Documents_CustomsDeclarations_CustomsDeclarationId",
                table: "Documents");

            migrationBuilder.DropIndex(
                name: "IX_Documents_CustomsDeclarationId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "CustomsDeclarationId",
                table: "Documents");

            migrationBuilder.DropTable(name: "CustomsDeclarations");
        }
    }
}
