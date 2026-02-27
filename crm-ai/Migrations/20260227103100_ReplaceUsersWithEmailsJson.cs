using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace crm_ai.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceUsersWithEmailsJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SelectionExecutionUsers");

            migrationBuilder.AddColumn<string>(
                name: "EmailsJson",
                table: "SelectionExecutions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailsJson",
                table: "SelectionExecutions");

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
                name: "IX_SelectionExecutionUsers_SelectionExecutionId",
                table: "SelectionExecutionUsers",
                column: "SelectionExecutionId");
        }
    }
}
