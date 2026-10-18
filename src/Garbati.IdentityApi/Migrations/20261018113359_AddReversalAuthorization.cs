using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Garbati.IdentityApi.Migrations
{
    /// <inheritdoc />
    public partial class AddReversalAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reason_code",
                table: "authorization_decisions",
                type: "text",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_authorization_decisions_decision_id",
                table: "authorization_decisions",
                column: "decision_id");

            migrationBuilder.CreateTable(
                name: "reversal_outcomes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    authorization_decision_id = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_by = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reversal_outcomes", x => x.id);
                    table.CheckConstraint("ck_reversal_outcomes_outcome", "\"outcome\" IN ('Completed', 'WriteOff')");
                    table.ForeignKey(
                        name: "fk_reversal_outcomes_authorization_decisions_authorization_dec",
                        column: x => x.authorization_decision_id,
                        principalTable: "authorization_decisions",
                        principalColumn: "decision_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reversal_outcomes_authorization_decision_id",
                table: "reversal_outcomes",
                column: "authorization_decision_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reversal_outcomes");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_authorization_decisions_decision_id",
                table: "authorization_decisions");

            migrationBuilder.DropColumn(
                name: "reason_code",
                table: "authorization_decisions");
        }
    }
}
