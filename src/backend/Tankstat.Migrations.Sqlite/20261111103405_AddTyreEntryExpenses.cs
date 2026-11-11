using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tankstat.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddTyreEntryExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExpenseId",
                table: "TyreEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ExpenseTrashedWithEntry",
                table: "TyreEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_TyreEntries_ExpenseId",
                table: "TyreEntries",
                column: "ExpenseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TyreEntries_ExpenseId",
                table: "TyreEntries");

            migrationBuilder.DropColumn(
                name: "ExpenseId",
                table: "TyreEntries");

            migrationBuilder.DropColumn(
                name: "ExpenseTrashedWithEntry",
                table: "TyreEntries");
        }
    }
}
