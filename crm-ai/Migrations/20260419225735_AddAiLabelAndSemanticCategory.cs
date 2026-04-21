using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace crm_ai.Migrations
{
    /// <inheritdoc />
    public partial class AddAiLabelAndSemanticCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiLabel",
                table: "TreeNodes",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SemanticCategory",
                table: "TreeNodes",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiLabel",
                table: "TreeNodes");

            migrationBuilder.DropColumn(
                name: "SemanticCategory",
                table: "TreeNodes");
        }
    }
}