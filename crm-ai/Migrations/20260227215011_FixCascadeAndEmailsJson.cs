using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace crm_ai.Migrations
{
    /// <inheritdoc />
    public partial class FixCascadeAndEmailsJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Selections",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                table: "Selections");
        }
    }
}
