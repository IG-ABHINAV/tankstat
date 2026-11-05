using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tankstat.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class SavedWidgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Range",
                table: "VehicleCharts",
                type: "TEXT",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "Metric",
                table: "VehicleCharts",
                type: "TEXT",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "Kind",
                table: "VehicleCharts",
                type: "TEXT",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "Grouping",
                table: "VehicleCharts",
                type: "TEXT",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 32);

            migrationBuilder.AddColumn<string>(
                name: "Measure",
                table: "VehicleCharts",
                type: "TEXT",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Period",
                table: "VehicleCharts",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Sparkline",
                table: "VehicleCharts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Template",
                table: "VehicleCharts",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Widget",
                table: "VehicleCharts",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Chart");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Measure",
                table: "VehicleCharts");

            migrationBuilder.DropColumn(
                name: "Period",
                table: "VehicleCharts");

            migrationBuilder.DropColumn(
                name: "Sparkline",
                table: "VehicleCharts");

            migrationBuilder.DropColumn(
                name: "Template",
                table: "VehicleCharts");

            migrationBuilder.DropColumn(
                name: "Widget",
                table: "VehicleCharts");

            migrationBuilder.AlterColumn<string>(
                name: "Range",
                table: "VehicleCharts",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Metric",
                table: "VehicleCharts",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Kind",
                table: "VehicleCharts",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Grouping",
                table: "VehicleCharts",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 32,
                oldNullable: true);
        }
    }
}
