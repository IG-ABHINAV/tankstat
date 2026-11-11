using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tankstat.Migrations.PostgreSql
{
    /// <summary>
    /// #288: a tyre set's time on the car is an installation, no longer a stretch. The table, its keys and indexes are renamed, never
    /// dropped (its rows stay), and so are the names stored as text: tombstones, the sync ledger's change kinds, reasons and payloads, and
    /// the arguments of the notifications about its parked changes.
    /// </summary>
    public partial class RenameTyreInstallations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "TyreStretches", newName: "TyreInstallations");
            migrationBuilder.RenameIndex(name: "IX_TyreStretches_DeletedAt", table: "TyreInstallations", newName: "IX_TyreInstallations_DeletedAt");
            migrationBuilder.RenameIndex(name: "IX_TyreStretches_OwnerId", table: "TyreInstallations", newName: "IX_TyreInstallations_OwnerId");
            migrationBuilder.RenameIndex(name: "IX_TyreStretches_VehicleId", table: "TyreInstallations", newName: "IX_TyreInstallations_VehicleId");
            migrationBuilder.RenameIndex(name: "IX_TyreStretches_SetId_From", table: "TyreInstallations", newName: "IX_TyreInstallations_SetId_From");
            migrationBuilder.RenameIndex(name: "IX_TyreStretches_VehicleId_UpdatedAt", table: "TyreInstallations", newName: "IX_TyreInstallations_VehicleId_UpdatedAt");
            migrationBuilder.Sql("ALTER TABLE \"TyreInstallations\" RENAME CONSTRAINT \"PK_TyreStretches\" TO \"PK_TyreInstallations\";");
            migrationBuilder.Sql("ALTER TABLE \"TyreInstallations\" RENAME CONSTRAINT \"FK_TyreStretches_TyreSets_SetId\" TO \"FK_TyreInstallations_TyreSets_SetId\";");
            // What was stored under the old name: tombstones, the sync ledger's kinds, reasons and payloads (#288).
            migrationBuilder.Sql(@"UPDATE ""Tombstones"" SET ""EntityType"" = 'TyreInstallation' WHERE ""EntityType"" = 'TyreStretch';");
            migrationBuilder.Sql(@"UPDATE ""SyncChanges"" SET ""Kind"" = REPLACE(""Kind"", 'TyreStretch', 'TyreInstallation') WHERE ""Kind"" LIKE '%TyreStretch';");
            migrationBuilder.Sql(@"UPDATE ""SyncChanges"" SET ""ReasonKey"" = REPLACE(""ReasonKey"", 'tyres.stretch', 'tyres.installation') WHERE ""ReasonKey"" LIKE 'tyres.stretch%';");
            migrationBuilder.Sql(@"UPDATE ""SyncChanges"" SET ""Payload"" = REPLACE(REPLACE(""Payload"", 'TyreStretch', 'TyreInstallation'), '""stretchId""', '""installationId""') WHERE (""Payload"" LIKE '%TyreStretch%' OR ""Payload"" LIKE '%""stretchId""%') AND LENGTH(REPLACE(REPLACE(""Payload"", 'TyreStretch', 'TyreInstallation'), '""stretchId""', '""installationId""')) <= 4000;");
            // A parked change's notification is worded from the change's kind and the reason's key, kept in its arguments (one JSON object).
            migrationBuilder.Sql(@"UPDATE ""Notifications"" SET ""Args"" = REPLACE(REPLACE(""Args"", '_TYRE_STRETCH""', '_TYRE_INSTALLATION""'), '""tyres.stretch', '""tyres.installation') WHERE ""Kind"" = 'SyncChangeParked' AND (""Args"" LIKE '%TYRE_STRETCH""%' OR ""Args"" LIKE '%""tyres.stretch%') AND LENGTH(REPLACE(REPLACE(""Args"", '_TYRE_STRETCH""', '_TYRE_INSTALLATION""'), '""tyres.stretch', '""tyres.installation')) <= 4000;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "TyreInstallations", newName: "TyreStretches");
            migrationBuilder.RenameIndex(name: "IX_TyreInstallations_DeletedAt", table: "TyreStretches", newName: "IX_TyreStretches_DeletedAt");
            migrationBuilder.RenameIndex(name: "IX_TyreInstallations_OwnerId", table: "TyreStretches", newName: "IX_TyreStretches_OwnerId");
            migrationBuilder.RenameIndex(name: "IX_TyreInstallations_VehicleId", table: "TyreStretches", newName: "IX_TyreStretches_VehicleId");
            migrationBuilder.RenameIndex(name: "IX_TyreInstallations_SetId_From", table: "TyreStretches", newName: "IX_TyreStretches_SetId_From");
            migrationBuilder.RenameIndex(name: "IX_TyreInstallations_VehicleId_UpdatedAt", table: "TyreStretches", newName: "IX_TyreStretches_VehicleId_UpdatedAt");
            migrationBuilder.Sql("ALTER TABLE \"TyreStretches\" RENAME CONSTRAINT \"PK_TyreInstallations\" TO \"PK_TyreStretches\";");
            migrationBuilder.Sql("ALTER TABLE \"TyreStretches\" RENAME CONSTRAINT \"FK_TyreInstallations_TyreSets_SetId\" TO \"FK_TyreStretches_TyreSets_SetId\";");
            // What was stored under the old name: tombstones, the sync ledger's kinds, reasons and payloads (#288).
            migrationBuilder.Sql(@"UPDATE ""Tombstones"" SET ""EntityType"" = 'TyreStretch' WHERE ""EntityType"" = 'TyreInstallation';");
            migrationBuilder.Sql(@"UPDATE ""SyncChanges"" SET ""Kind"" = REPLACE(""Kind"", 'TyreInstallation', 'TyreStretch') WHERE ""Kind"" LIKE '%TyreInstallation';");
            migrationBuilder.Sql(@"UPDATE ""SyncChanges"" SET ""ReasonKey"" = REPLACE(""ReasonKey"", 'tyres.installation', 'tyres.stretch') WHERE ""ReasonKey"" LIKE 'tyres.installation%';");
            migrationBuilder.Sql(@"UPDATE ""SyncChanges"" SET ""Payload"" = REPLACE(REPLACE(""Payload"", 'TyreInstallation', 'TyreStretch'), '""installationId""', '""stretchId""') WHERE (""Payload"" LIKE '%TyreInstallation%' OR ""Payload"" LIKE '%""installationId""%') AND LENGTH(REPLACE(REPLACE(""Payload"", 'TyreInstallation', 'TyreStretch'), '""installationId""', '""stretchId""')) <= 4000;");
            migrationBuilder.Sql(@"UPDATE ""Notifications"" SET ""Args"" = REPLACE(REPLACE(""Args"", '_TYRE_INSTALLATION""', '_TYRE_STRETCH""'), '""tyres.installation', '""tyres.stretch') WHERE ""Kind"" = 'SyncChangeParked' AND (""Args"" LIKE '%TYRE_INSTALLATION""%' OR ""Args"" LIKE '%""tyres.installation%');");
        }
    }
}
