using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Garbati.IdentityApi.Migrations
{
    /// <inheritdoc />
    public partial class AddJointApprovalWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_approval_requests",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    request_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    ledger_account_id = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: true),
                    debit_account_id = table.Column<string>(type: "text", nullable: true),
                    credit_account_id = table.Column<string>(type: "text", nullable: true),
                    amount = table.Column<string>(type: "text", nullable: true),
                    currency = table.Column<string>(type: "text", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: true),
                    requested_required_approvals = table.Column<int>(type: "integer", nullable: true),
                    required_approvals = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decision_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_approval_requests", x => x.id);
                    table.CheckConstraint("ck_pending_approval_requests_kind", "\"kind\" IN ('Debit', 'ThresholdChange')");
                    table.CheckConstraint("ck_pending_approval_requests_status", "\"status\" IN ('Pending', 'Approved', 'Expired', 'Cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "approval_records",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    pending_approval_request_id = table.Column<long>(type: "bigint", nullable: false),
                    customer_id = table.Column<string>(type: "text", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    voided_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_approval_records_pending_approval_requests_pending_approval",
                        column: x => x.pending_approval_request_id,
                        principalTable: "pending_approval_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approval_records_request_customer",
                table: "approval_records",
                columns: new[] { "pending_approval_request_id", "customer_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pending_approval_requests_ledger_account_id",
                table: "pending_approval_requests",
                column: "ledger_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_pending_approval_requests_request_id",
                table: "pending_approval_requests",
                column: "request_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approval_records");

            migrationBuilder.DropTable(
                name: "pending_approval_requests");
        }
    }
}
