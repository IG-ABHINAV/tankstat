using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tankstat.Migrations.Sqlite
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TyreId",
                table: "TyreEntries",
                type: "TEXT",
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
            migrationBuilder.Sql("""UPDATE "TyreEntries" SET "OdometerReadingId" = NULL WHERE "OdometerReadingId" IS NOT NULL AND EXISTS (SELECT 1 FROM "TyreEntries" o WHERE o."OdometerReadingId" = "TyreEntries"."OdometerReadingId" AND o."Id" < "TyreEntries"."Id");""");
            migrationBuilder.CreateIndex(
                name: "IX_TyreEntries_OdometerReadingId",
                table: "TyreEntries",
                column: "OdometerReadingId",
                unique: true);
        }
    }
}
