using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace crm_ai.Migrations
{
    /// <inheritdoc />
    public partial class AddSelectionSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SelectionExecutions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SelectionId = table.Column<int>(type: "int", nullable: false),
                    ExecutedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TotalUsers = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelectionExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SelectionExecutions_Selections_SelectionId",
                        column: x => x.SelectionId,
                        principalTable: "Selections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SelectionExecutionUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SelectionExecutionId = table.Column<int>(type: "int", nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelectionExecutionUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SelectionExecutionUsers_SelectionExecutions_SelectionExecutionId",
                        column: x => x.SelectionExecutionId,
                        principalTable: "SelectionExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SelectionExecutions_SelectionId",
                table: "SelectionExecutions",
                column: "SelectionId");

            migrationBuilder.CreateIndex(
                name: "IX_SelectionExecutionUsers_SelectionExecutionId",
                table: "SelectionExecutionUsers",
                column: "SelectionExecutionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SelectionExecutionUsers");

            migrationBuilder.DropTable(
                name: "SelectionExecutions");
        }
    }
}
