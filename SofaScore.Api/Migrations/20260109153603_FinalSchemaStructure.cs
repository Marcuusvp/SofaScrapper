using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SofaScore.Api.Migrations
{
    /// <inheritdoc />
    public partial class FinalSchemaStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EnrichmentAttempts",
                table: "Matches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastEnrichmentAttempt",
                table: "Matches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastEnrichmentError",
                table: "Matches",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProcessingStatus",
                table: "Matches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "RoundStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TournamentId = table.Column<int>(type: "integer", nullable: false),
                    SeasonId = table.Column<int>(type: "integer", nullable: false),
                    Round = table.Column<int>(type: "integer", nullable: false),
                    IsFullyProcessed = table.Column<bool>(type: "boolean", nullable: false),
                    TotalMatches = table.Column<int>(type: "integer", nullable: false),
                    EnrichedMatches = table.Column<int>(type: "integer", nullable: false),
                    PostponedMatches = table.Column<int>(type: "integer", nullable: false),
                    CancelledMatches = table.Column<int>(type: "integer", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedBy = table.Column<string>(type: "text", nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastCheck = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoundStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Matches_TournamentId_Round_ProcessingStatus",
                table: "Matches",
                columns: new[] { "TournamentId", "Round", "ProcessingStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_RoundStates_IsFullyProcessed_LastCheck",
                table: "RoundStates",
                columns: new[] { "IsFullyProcessed", "LastCheck" });

            migrationBuilder.CreateIndex(
                name: "IX_RoundStates_TournamentId_SeasonId_Round",
                table: "RoundStates",
                columns: new[] { "TournamentId", "SeasonId", "Round" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoundStates");

            migrationBuilder.DropIndex(
                name: "IX_Matches_TournamentId_Round_ProcessingStatus",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "EnrichmentAttempts",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "LastEnrichmentAttempt",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "LastEnrichmentError",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "ProcessingStatus",
                table: "Matches");
        }
    }
}
