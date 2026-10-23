using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Garbati.IdentityApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBvnNinToCustomerIdentityData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bvn",
                table: "customer_identity_data",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "nin",
                table: "customer_identity_data",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bvn",
                table: "customer_identity_data");

            migrationBuilder.DropColumn(
                name: "nin",
                table: "customer_identity_data");
        }
    }
}
