using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tankstat.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddTyres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TyreLogDays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OdometerReadingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValue: new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChangedById = table.Column<Guid>(type: "uuid", nullable: true),
                    LastChange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
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
                });

            migrationBuilder.CreateTable(
                name: "TyreSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Season = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Size = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Since = table.Column<DateOnly>(type: "date", nullable: false),
                    MadeWeek = table.Column<int>(type: "integer", nullable: true),
                    MadeYear = table.Column<int>(type: "integer", nullable: true),
                    RoughDistance = table.Column<long>(type: "bigint", nullable: false),
                    RoughUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    RetiredOn = table.Column<DateOnly>(type: "date", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValue: new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChangedById = table.Column<Guid>(type: "uuid", nullable: true),
                    LastChange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TyreSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TyreSets_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TyreStretches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: false),
                    SetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    Until = table.Column<DateOnly>(type: "date", nullable: true),
                    PairOneFront = table.Column<bool>(type: "boolean", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValue: new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChangedById = table.Column<Guid>(type: "uuid", nullable: true),
                    LastChange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TyreStretches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TyreStretches_TyreSets_SetId",
                        column: x => x.SetId,
                        principalTable: "TyreSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateIndex(
                name: "IX_TyreSets_DeletedAt",
                table: "TyreSets",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TyreSets_OwnerId",
                table: "TyreSets",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_TyreSets_VehicleId_UpdatedAt",
                table: "TyreSets",
                columns: new[] { "VehicleId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TyreStretches_DeletedAt",
                table: "TyreStretches",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TyreStretches_OwnerId",
                table: "TyreStretches",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_TyreStretches_SetId_From",
                table: "TyreStretches",
                columns: new[] { "SetId", "From" });

            migrationBuilder.CreateIndex(
                name: "IX_TyreStretches_VehicleId",
                table: "TyreStretches",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_TyreStretches_VehicleId_UpdatedAt",
                table: "TyreStretches",
                columns: new[] { "VehicleId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TyreLogDays");

            migrationBuilder.DropTable(
                name: "TyreStretches");

            migrationBuilder.DropTable(
                name: "TyreSets");
        }
    }
}
