using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SofaScore.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Matches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    TournamentId = table.Column<int>(type: "integer", nullable: false),
                    SeasonId = table.Column<int>(type: "integer", nullable: false),
                    Round = table.Column<int>(type: "integer", nullable: false),
                    HomeTeam = table.Column<string>(type: "text", nullable: false),
                    AwayTeam = table.Column<string>(type: "text", nullable: false),
                    HomeScore = table.Column<int>(type: "integer", nullable: false),
                    AwayScore = table.Column<int>(type: "integer", nullable: false),
                    StartTimestamp = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Stadium = table.Column<string>(type: "text", nullable: true),
                    Referee = table.Column<string>(type: "text", nullable: true),
                    Attendance = table.Column<int>(type: "integer", nullable: true),
                    ProcessingStatus = table.Column<int>(type: "integer", nullable: false),
                    EnrichmentAttempts = table.Column<int>(type: "integer", nullable: false),
                    LastEnrichmentAttempt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastEnrichmentError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Matches", x => x.Id);
                });

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

            migrationBuilder.CreateTable(
                name: "Incidents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MatchId = table.Column<int>(type: "integer", nullable: false),
                    IncidentType = table.Column<string>(type: "text", nullable: false),
                    IncidentClass = table.Column<string>(type: "text", nullable: true),
                    Time = table.Column<int>(type: "integer", nullable: false),
                    AddedTime = table.Column<int>(type: "integer", nullable: false),
                    IsHome = table.Column<bool>(type: "boolean", nullable: false),
                    PlayerName = table.Column<string>(type: "text", nullable: true),
                    AssistName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Incidents_Matches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "Matches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MatchStats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MatchId = table.Column<int>(type: "integer", nullable: false),
                    Period = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    HomeValue = table.Column<string>(type: "text", nullable: false),
                    AwayValue = table.Column<string>(type: "text", nullable: false),
                    CompareCode = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchStats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchStats_Matches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "Matches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_MatchId",
                table: "Incidents",
                column: "MatchId");

            migrationBuilder.CreateIndex(
                name: "IX_Matches_TournamentId_Round_ProcessingStatus",
                table: "Matches",
                columns: new[] { "TournamentId", "Round", "ProcessingStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchStats_MatchId",
                table: "MatchStats",
                column: "MatchId");

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
                name: "Incidents");

            migrationBuilder.DropTable(
                name: "MatchStats");

            migrationBuilder.DropTable(
                name: "RoundStates");

            migrationBuilder.DropTable(
                name: "Matches");
        }
    }
}
