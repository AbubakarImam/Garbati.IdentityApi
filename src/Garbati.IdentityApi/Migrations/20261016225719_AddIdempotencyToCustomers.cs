using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Garbati.IdentityApi.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotencyToCustomers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                table: "customers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "initiated_by",
                table: "customers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "request_hash",
                table: "customers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_customers_initiated_by_idempotency_key",
                table: "customers",
                columns: new[] { "initiated_by", "idempotency_key" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_customers_initiated_by_idempotency_key",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "initiated_by",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "request_hash",
                table: "customers");
        }
    }
}
