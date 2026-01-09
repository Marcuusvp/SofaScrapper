using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SofaScore.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DbIncident_Matches_MatchId",
                table: "DbIncident");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DbIncident",
                table: "DbIncident");

            migrationBuilder.RenameTable(
                name: "DbIncident",
                newName: "Incidents");

            migrationBuilder.RenameIndex(
                name: "IX_DbIncident_MatchId",
                table: "Incidents",
                newName: "IX_Incidents_MatchId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Incidents",
                table: "Incidents",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Incidents_Matches_MatchId",
                table: "Incidents",
                column: "MatchId",
                principalTable: "Matches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Incidents_Matches_MatchId",
                table: "Incidents");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Incidents",
                table: "Incidents");

            migrationBuilder.RenameTable(
                name: "Incidents",
                newName: "DbIncident");

            migrationBuilder.RenameIndex(
                name: "IX_Incidents_MatchId",
                table: "DbIncident",
                newName: "IX_DbIncident_MatchId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DbIncident",
                table: "DbIncident",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_DbIncident_Matches_MatchId",
                table: "DbIncident",
                column: "MatchId",
                principalTable: "Matches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
