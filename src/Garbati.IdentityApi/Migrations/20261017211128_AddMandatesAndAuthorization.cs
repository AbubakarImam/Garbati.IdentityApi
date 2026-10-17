using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Garbati.IdentityApi.Migrations
{
    /// <inheritdoc />
    public partial class AddMandatesAndAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "authorization_decisions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    decision_id = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    debit_account_id = table.Column<string>(type: "text", nullable: false),
                    credit_account_id = table.Column<string>(type: "text", nullable: true),
                    amount = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    mandate_id = table.Column<string>(type: "text", nullable: true),
                    approved = table.Column<bool>(type: "boolean", nullable: false),
                    denial_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    grant_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_authorization_decisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mandate_approval_policies",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ledger_account_id = table.Column<string>(type: "text", nullable: false),
                    required_approvals = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_changed_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mandate_approval_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mandates",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    mandate_id = table.Column<string>(type: "text", nullable: false),
                    customer_id = table.Column<long>(type: "bigint", nullable: false),
                    ledger_account_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<string>(type: "text", nullable: true),
                    revocation_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mandates", x => x.id);
                    table.CheckConstraint("ck_mandates_status", "\"status\" IN ('Active', 'Revoked')");
                    table.ForeignKey(
                        name: "fk_mandates_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mandate_permissions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    mandate_id = table.Column<long>(type: "bigint", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mandate_permissions", x => x.id);
                    table.CheckConstraint("ck_mandate_permissions_operation", "\"operation\" IN ('View', 'Debit', 'Credit')");
                    table.ForeignKey(
                        name: "fk_mandate_permissions_mandates_mandate_id",
                        column: x => x.mandate_id,
                        principalTable: "mandates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_authorization_decisions_decision_id",
                table: "authorization_decisions",
                column: "decision_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mandate_approval_policies_ledger_account_id",
                table: "mandate_approval_policies",
                column: "ledger_account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mandate_permissions_mandate_id_operation",
                table: "mandate_permissions",
                columns: new[] { "mandate_id", "operation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mandates_customer_account_active",
                table: "mandates",
                columns: new[] { "customer_id", "ledger_account_id" },
                unique: true,
                filter: "\"status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_mandates_ledger_account_id",
                table: "mandates",
                column: "ledger_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_mandates_mandate_id",
                table: "mandates",
                column: "mandate_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "authorization_decisions");

            migrationBuilder.DropTable(
                name: "mandate_approval_policies");

            migrationBuilder.DropTable(
                name: "mandate_permissions");

            migrationBuilder.DropTable(
                name: "mandates");
        }
    }
}
