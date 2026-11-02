using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tankstat.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddDashboardLayouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserDashboardLayouts",
                columns: table => new
                {
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Figures = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Charts = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Hidden = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserDashboardLayouts", x => new { x.UserId, x.VehicleId });
                    table.ForeignKey(
                        name: "FK_UserDashboardLayouts_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VehicleDashboardLayouts",
                columns: table => new
                {
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Figures = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Charts = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Hidden = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleDashboardLayouts", x => x.VehicleId);
                    table.ForeignKey(
                        name: "FK_VehicleDashboardLayouts_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserDashboardLayouts_VehicleId",
                table: "UserDashboardLayouts",
                column: "VehicleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserDashboardLayouts");

            migrationBuilder.DropTable(
                name: "VehicleDashboardLayouts");
        }
    }
}
