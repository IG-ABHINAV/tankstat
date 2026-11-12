using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tankstat.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddSingleTyres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "BrokenUpOn",
                table: "TyreSets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Tyres",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VehicleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedById = table.Column<Guid>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 6, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    MadeWeek = table.Column<int>(type: "INTEGER", nullable: true),
                    MadeYear = table.Column<int>(type: "INTEGER", nullable: true),
                    Since = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RoughDistance = table.Column<long>(type: "INTEGER", nullable: false),
                    RoughUntil = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    RetiredOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    RetiredWhy = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ReplacedById = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValue: new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)),
                    ChangedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ChangedById = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastChange = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tyres", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tyres_TyreSets_SetId",
                        column: x => x.SetId,
                        principalTable: "TyreSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TyrePlaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VehicleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TyreId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedById = table.Column<Guid>(type: "TEXT", nullable: false),
                    From = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Until = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Corner = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValue: new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)),
                    ChangedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ChangedById = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastChange = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TyrePlaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TyrePlaces_Tyres_TyreId",
                        column: x => x.TyreId,
                        principalTable: "Tyres",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TyrePlaces_DeletedAt",
                table: "TyrePlaces",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TyrePlaces_InstallationId",
                table: "TyrePlaces",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_TyrePlaces_OwnerId",
                table: "TyrePlaces",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_TyrePlaces_TyreId_From",
                table: "TyrePlaces",
                columns: new[] { "TyreId", "From" });

            migrationBuilder.CreateIndex(
                name: "IX_TyrePlaces_VehicleId_UpdatedAt",
                table: "TyrePlaces",
                columns: new[] { "VehicleId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tyres_DeletedAt",
                table: "Tyres",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Tyres_OwnerId",
                table: "Tyres",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Tyres_SetId",
                table: "Tyres",
                column: "SetId");

            migrationBuilder.CreateIndex(
                name: "IX_Tyres_VehicleId",
                table: "Tyres",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_Tyres_VehicleId_UpdatedAt",
                table: "Tyres",
                columns: new[] { "VehicleId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TyrePlaces");

            migrationBuilder.DropTable(
                name: "Tyres");

            migrationBuilder.DropColumn(
                name: "BrokenUpOn",
                table: "TyreSets");
        }
    }
}
