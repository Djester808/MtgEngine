using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class SynergyScoreVersionedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CardSynergyScores_CommanderOracleId_CardOracleId",
                table: "CardSynergyScores");

            migrationBuilder.CreateIndex(
                name: "IX_CardSynergyScores_CommanderOracleId_CardOracleId_ModelVersion",
                table: "CardSynergyScores",
                columns: new[] { "CommanderOracleId", "CardOracleId", "ModelVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CardSynergyScores_CommanderOracleId_CardOracleId_ModelVersion",
                table: "CardSynergyScores");

            migrationBuilder.CreateIndex(
                name: "IX_CardSynergyScores_CommanderOracleId_CardOracleId",
                table: "CardSynergyScores",
                columns: new[] { "CommanderOracleId", "CardOracleId" },
                unique: true);
        }
    }
}
