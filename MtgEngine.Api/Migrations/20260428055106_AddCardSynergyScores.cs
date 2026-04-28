using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCardSynergyScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CardSynergyScores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommanderOracleId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CardOracleId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ModelVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardSynergyScores", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CardSynergyScores_CommanderOracleId_CardOracleId",
                table: "CardSynergyScores",
                columns: new[] { "CommanderOracleId", "CardOracleId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CardSynergyScores");
        }
    }
}
