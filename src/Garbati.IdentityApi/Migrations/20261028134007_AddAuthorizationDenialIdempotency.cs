using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Garbati.IdentityApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthorizationDenialIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_authorization_decisions_requested_by_idempotency_key_denied",
                table: "authorization_decisions",
                columns: new[] { "requested_by", "idempotency_key" },
                unique: true,
                filter: "approved = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_authorization_decisions_requested_by_idempotency_key_denied",
                table: "authorization_decisions");
        }
    }
}
