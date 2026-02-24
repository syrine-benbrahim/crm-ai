using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace crm_ai.Migrations
{
    public partial class RenameCustomerKeys : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1️⃣ Drop old foreign keys (pointing to CustomerCtcID)
            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_Customers_CustomerCtcID",
                table: "Bookings");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomerAddresses_Customers_CustomerCtcID",
                table: "CustomerAddresses");

            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_Customers_CustomerCtcID",
                table: "Transactions");

            migrationBuilder.DropForeignKey(
                name: "FK_Visits_Customers_CustomerCtcID",
                table: "Visits");


            // 2️⃣ Rename PK in Customers
            migrationBuilder.RenameColumn(
                name: "CtcID",
                table: "Customers",
                newName: "Id");


            // 3️⃣ Rename shadow FK columns to proper CustomerId

            migrationBuilder.RenameColumn(
                name: "CustomerCtcID",
                table: "Visits",
                newName: "CustomerId");

            migrationBuilder.RenameIndex(
                name: "IX_Visits_CustomerCtcID",
                table: "Visits",
                newName: "IX_Visits_CustomerId");


            migrationBuilder.RenameColumn(
                name: "CustomerCtcID",
                table: "Transactions",
                newName: "CustomerId");

            migrationBuilder.RenameIndex(
                name: "IX_Transactions_CustomerCtcID",
                table: "Transactions",
                newName: "IX_Transactions_CustomerId");


            migrationBuilder.RenameColumn(
                name: "CustomerCtcID",
                table: "CustomerAddresses",
                newName: "CustomerId");

            migrationBuilder.RenameIndex(
                name: "IX_CustomerAddresses_CustomerCtcID",
                table: "CustomerAddresses",
                newName: "IX_CustomerAddresses_CustomerId");


            migrationBuilder.RenameColumn(
                name: "CustomerCtcID",
                table: "Bookings",
                newName: "CustomerId");

            migrationBuilder.RenameIndex(
                name: "IX_Bookings_CustomerCtcID",
                table: "Bookings",
                newName: "IX_Bookings_CustomerId");


            // 4️⃣ Drop the WRONG duplicate CtcID columns in child tables
            migrationBuilder.DropColumn(
                name: "CtcID",
                table: "Visits");

            migrationBuilder.DropColumn(
                name: "CtcID",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "CtcID",
                table: "CustomerAddresses");

            migrationBuilder.DropColumn(
                name: "CtcID",
                table: "Bookings");


            // 5️⃣ Recreate foreign keys using new structure
            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_Customers_CustomerId",
                table: "Bookings",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerAddresses_Customers_CustomerId",
                table: "CustomerAddresses",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_Customers_CustomerId",
                table: "Transactions",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Visits_Customers_CustomerId",
                table: "Visits",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse everything

            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_Customers_CustomerId",
                table: "Bookings");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomerAddresses_Customers_CustomerId",
                table: "CustomerAddresses");

            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_Customers_CustomerId",
                table: "Transactions");

            migrationBuilder.DropForeignKey(
                name: "FK_Visits_Customers_CustomerId",
                table: "Visits");


            // Re-add dropped CtcID columns
            migrationBuilder.AddColumn<int>(
                name: "CtcID",
                table: "Visits",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CtcID",
                table: "Transactions",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CtcID",
                table: "CustomerAddresses",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CtcID",
                table: "Bookings",
                nullable: false,
                defaultValue: 0);


            // Rename columns back
            migrationBuilder.RenameColumn(
                name: "Id",
                table: "Customers",
                newName: "CtcID");

            migrationBuilder.RenameColumn(
                name: "CustomerId",
                table: "Visits",
                newName: "CustomerCtcID");

            migrationBuilder.RenameColumn(
                name: "CustomerId",
                table: "Transactions",
                newName: "CustomerCtcID");

            migrationBuilder.RenameColumn(
                name: "CustomerId",
                table: "CustomerAddresses",
                newName: "CustomerCtcID");

            migrationBuilder.RenameColumn(
                name: "CustomerId",
                table: "Bookings",
                newName: "CustomerCtcID");
        }
    }
}