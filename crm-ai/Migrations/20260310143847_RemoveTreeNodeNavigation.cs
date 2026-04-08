using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace crm_ai.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTreeNodeNavigation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SelectionRules_TreeNodes_TreeNodeId",
                table: "SelectionRules");

            migrationBuilder.DropIndex(
                name: "IX_SelectionRules_TreeNodeId",
                table: "SelectionRules");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SelectionRules_TreeNodeId",
                table: "SelectionRules",
                column: "TreeNodeId");

            migrationBuilder.AddForeignKey(
                name: "FK_SelectionRules_TreeNodes_TreeNodeId",
                table: "SelectionRules",
                column: "TreeNodeId",
                principalTable: "TreeNodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
