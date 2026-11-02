using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tankstat.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddChartTrends : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CompareYearEarlier",
                table: "VehicleCharts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RunningTotal",
                table: "VehicleCharts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Trend",
                table: "VehicleCharts",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompareYearEarlier",
                table: "VehicleCharts");

            migrationBuilder.DropColumn(
                name: "RunningTotal",
                table: "VehicleCharts");

            migrationBuilder.DropColumn(
                name: "Trend",
                table: "VehicleCharts");
        }
    }
}
