using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tankstat.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddTyreEntryTyres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TyreEntries_OdometerReadingId",
                table: "TyreEntries");

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "TyreEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TyreId",
                table: "TyreEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TyreEntries_GroupId",
                table: "TyreEntries",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_TyreEntries_OdometerReadingId",
                table: "TyreEntries",
                column: "OdometerReadingId");

            migrationBuilder.CreateIndex(
                name: "IX_TyreEntries_TyreId",
                table: "TyreEntries",
                column: "TyreId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TyreEntries_GroupId",
                table: "TyreEntries");

            migrationBuilder.DropIndex(
                name: "IX_TyreEntries_OdometerReadingId",
                table: "TyreEntries");

            migrationBuilder.DropIndex(
                name: "IX_TyreEntries_TyreId",
                table: "TyreEntries");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "TyreEntries");

            migrationBuilder.DropColumn(
                name: "TyreId",
                table: "TyreEntries");

            // Lossy (#289): the lines made together held one reading row; a unique index again leaves it on one of them only.
            migrationBuilder.Sql("""UPDATE e SET OdometerReadingId = NULL FROM TyreEntries e WHERE e.OdometerReadingId IS NOT NULL AND EXISTS (SELECT 1 FROM TyreEntries o WHERE o.OdometerReadingId = e.OdometerReadingId AND o.Id < e.Id);""");
            migrationBuilder.CreateIndex(
                name: "IX_TyreEntries_OdometerReadingId",
                table: "TyreEntries",
                column: "OdometerReadingId",
                unique: true,
                filter: "[OdometerReadingId] IS NOT NULL");
        }
    }
}
