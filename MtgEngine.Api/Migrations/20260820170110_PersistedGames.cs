using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class PersistedGames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PersistedGames",
                columns: table => new
                {
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Log = table.Column<string>(type: "TEXT", nullable: false),
                    LastActivityUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsOver = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersistedGames", x => x.GameId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PersistedGames_LastActivityUtc",
                table: "PersistedGames",
                column: "LastActivityUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PersistedGames");
        }
    }
}
