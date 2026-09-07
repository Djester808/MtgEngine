using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class GameLogAndReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EventIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    OracleId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PersistedGames",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AllowHonourSystem = table.Column<bool>(type: "INTEGER", nullable: false),
                    SeatsJson = table.Column<string>(type: "TEXT", nullable: false),
                    LogJson = table.Column<string>(type: "TEXT", nullable: false),
                    EventCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Finished = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersistedGames", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameReports_GameId",
                table: "GameReports",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_GameReports_OracleId_CreatedAt",
                table: "GameReports",
                columns: new[] { "OracleId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PersistedGames_Finished_UpdatedAt",
                table: "PersistedGames",
                columns: new[] { "Finished", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameReports");

            migrationBuilder.DropTable(
                name: "PersistedGames");
        }
    }
}
