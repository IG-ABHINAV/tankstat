using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tankstat.Migrations.MySql
{
    /// <inheritdoc />
    public partial class AddTyreEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TyreEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    OwnerId = table.Column<Guid>(type: "char(36)", nullable: false),
                    VehicleId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CreatedById = table.Column<Guid>(type: "char(36)", nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    SetId = table.Column<Guid>(type: "char(36)", nullable: true),
                    InstallationId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    OdometerReadingId = table.Column<Guid>(type: "char(36)", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValue: new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)),
                    ChangedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ChangedById = table.Column<Guid>(type: "char(36)", nullable: true),
                    LastChange = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TyreEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TyreEntries_OdometerReadings_OdometerReadingId",
                        column: x => x.OdometerReadingId,
                        principalTable: "OdometerReadings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TyreEntries_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_TyreEntries_DeletedAt",
                table: "TyreEntries",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TyreEntries_OdometerReadingId",
                table: "TyreEntries",
                column: "OdometerReadingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TyreEntries_OwnerId",
                table: "TyreEntries",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_TyreEntries_SetId",
                table: "TyreEntries",
                column: "SetId");

            migrationBuilder.CreateIndex(
                name: "IX_TyreEntries_VehicleId_Day",
                table: "TyreEntries",
                columns: new[] { "VehicleId", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_TyreEntries_VehicleId_UpdatedAt",
                table: "TyreEntries",
                columns: new[] { "VehicleId", "UpdatedAt" });

            // #288: every installation gets its line, with its id (a change of positions when the same set came off that day); a day's
            // line goes onto the line of the live installation that starts that day, else it becomes a Note line keeping its reading.
            migrationBuilder.Sql(@"INSERT INTO `TyreEntries` (`Id`, `OwnerId`, `VehicleId`, `CreatedById`, `Kind`, `SetId`, `InstallationId`, `Day`, `Note`, `OdometerReadingId`, `DeletedAt`, `Version`, `UpdatedAt`, `ChangedAt`, `ChangedById`, `LastChange`) SELECT i.`Id`, i.`OwnerId`, i.`VehicleId`, i.`CreatedById`, CASE WHEN EXISTS (SELECT 1 FROM `TyreInstallations` p WHERE p.`SetId` = i.`SetId` AND p.`Id` <> i.`Id` AND p.`DeletedAt` IS NULL AND p.`Until` = i.`From`) THEN 'PositionsChanged' ELSE 'Swap' END, i.`SetId`, i.`Id`, i.`From`, d.`Note`, d.`OdometerReadingId`, i.`DeletedAt`, 1, i.`UpdatedAt`, i.`ChangedAt`, i.`ChangedById`, i.`LastChange` FROM `TyreInstallations` i LEFT JOIN `TyreLogDays` d ON d.`VehicleId` = i.`VehicleId` AND d.`Day` = i.`From` AND i.`DeletedAt` IS NULL AND EXISTS (SELECT 1 FROM `TyreSets` s WHERE s.`Id` = i.`SetId` AND s.`DeletedAt` IS NULL)");
            migrationBuilder.Sql(@"INSERT INTO `TyreEntries` (`Id`, `OwnerId`, `VehicleId`, `CreatedById`, `Kind`, `SetId`, `InstallationId`, `Day`, `Note`, `OdometerReadingId`, `DeletedAt`, `Version`, `UpdatedAt`, `ChangedAt`, `ChangedById`, `LastChange`) SELECT d.`Id`, d.`OwnerId`, d.`VehicleId`, d.`CreatedById`, 'Note', NULL, NULL, d.`Day`, d.`Note`, d.`OdometerReadingId`, NULL, d.`Version`, d.`UpdatedAt`, d.`ChangedAt`, d.`ChangedById`, d.`LastChange` FROM `TyreLogDays` d WHERE NOT EXISTS (SELECT 1 FROM `TyreInstallations` li INNER JOIN `TyreSets` ls ON ls.`Id` = li.`SetId` WHERE li.`VehicleId` = d.`VehicleId` AND li.`From` = d.`Day` AND li.`DeletedAt` IS NULL AND ls.`DeletedAt` IS NULL)");
            migrationBuilder.Sql(@"DELETE FROM `SyncChanges` WHERE `Kind` = 'SetTyreLogDay'");
            migrationBuilder.Sql(@"DELETE FROM `Tombstones` WHERE `EntityType` = 'TyreLogDay'");

            migrationBuilder.DropTable(
                name: "TyreLogDays");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TyreLogDays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    OdometerReadingId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ChangedById = table.Column<Guid>(type: "char(36)", nullable: true),
                    CreatedById = table.Column<Guid>(type: "char(36)", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    LastChange = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    Note = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    OwnerId = table.Column<Guid>(type: "char(36)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValue: new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)),
                    VehicleId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TyreLogDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TyreLogDays_OdometerReadings_OdometerReadingId",
                        column: x => x.OdometerReadingId,
                        principalTable: "OdometerReadings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TyreLogDays_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_TyreLogDays_OdometerReadingId",
                table: "TyreLogDays",
                column: "OdometerReadingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TyreLogDays_OwnerId",
                table: "TyreLogDays",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_TyreLogDays_VehicleId_Day",
                table: "TyreLogDays",
                columns: new[] { "VehicleId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TyreLogDays_VehicleId_UpdatedAt",
                table: "TyreLogDays",
                columns: new[] { "VehicleId", "UpdatedAt" });

            // Lossy: a day keeps the reading and note of its first live line that has one; trashed lines and the others go.
            migrationBuilder.Sql(@"INSERT INTO `TyreLogDays` (`Id`, `OdometerReadingId`, `ChangedAt`, `ChangedById`, `CreatedById`, `Day`, `LastChange`, `Note`, `OwnerId`, `UpdatedAt`, `VehicleId`, `Version`) SELECT e.`Id`, e.`OdometerReadingId`, e.`ChangedAt`, e.`ChangedById`, e.`CreatedById`, e.`Day`, e.`LastChange`, e.`Note`, e.`OwnerId`, e.`UpdatedAt`, e.`VehicleId`, e.`Version` FROM `TyreEntries` e WHERE e.`DeletedAt` IS NULL AND (e.`OdometerReadingId` IS NOT NULL OR e.`Note` IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM `TyreEntries` o WHERE o.`VehicleId` = e.`VehicleId` AND o.`Day` = e.`Day` AND o.`DeletedAt` IS NULL AND (o.`OdometerReadingId` IS NOT NULL OR o.`Note` IS NOT NULL) AND o.`Id` < e.`Id`)");
            migrationBuilder.Sql(@"DELETE FROM `SyncChanges` WHERE `Kind` IN ('AddTyreEntry', 'UpdateTyreEntry', 'DeleteTyreEntry', 'RestoreTyreEntry')");
            migrationBuilder.Sql(@"DELETE FROM `Tombstones` WHERE `EntityType` = 'TyreEntry'");

            migrationBuilder.DropTable(
                name: "TyreEntries");
        }
    }
}
