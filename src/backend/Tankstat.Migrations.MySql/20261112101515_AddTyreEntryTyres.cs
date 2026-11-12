using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tankstat.Migrations.MySql
{
    /// <inheritdoc />
    public partial class AddTyreEntryTyres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MySQL keeps an index a foreign key needs: the key goes first and comes back after.
            migrationBuilder.DropForeignKey(
                name: "FK_TyreEntries_OdometerReadings_OdometerReadingId",
                table: "TyreEntries");

            migrationBuilder.DropIndex(
                name: "IX_TyreEntries_OdometerReadingId",
                table: "TyreEntries");

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "TyreEntries",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TyreId",
                table: "TyreEntries",
                type: "char(36)",
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

            migrationBuilder.AddForeignKey(
                name: "FK_TyreEntries_OdometerReadings_OdometerReadingId",
                table: "TyreEntries",
                column: "OdometerReadingId",
                principalTable: "OdometerReadings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TyreEntries_OdometerReadings_OdometerReadingId",
                table: "TyreEntries");

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
            migrationBuilder.Sql("""UPDATE TyreEntries e JOIN (SELECT OdometerReadingId, MIN(Id) AS KeptBy FROM TyreEntries WHERE OdometerReadingId IS NOT NULL GROUP BY OdometerReadingId HAVING COUNT(*) > 1) d ON e.OdometerReadingId = d.OdometerReadingId AND e.Id <> d.KeptBy SET e.OdometerReadingId = NULL;""");
            migrationBuilder.CreateIndex(
                name: "IX_TyreEntries_OdometerReadingId",
                table: "TyreEntries",
                column: "OdometerReadingId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_TyreEntries_OdometerReadings_OdometerReadingId",
                table: "TyreEntries",
                column: "OdometerReadingId",
                principalTable: "OdometerReadings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
