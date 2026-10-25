using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class GameTableLobbies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameLobbies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameLobbies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GameLobbyMembers",
                columns: table => new
                {
                    LobbyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameLobbyMembers", x => new { x.LobbyId, x.UserId });
                    table.ForeignKey(
                        name: "FK_GameLobbyMembers_GameLobbies_LobbyId",
                        column: x => x.LobbyId,
                        principalTable: "GameLobbies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameLobbies_UpdatedAt",
                table: "GameLobbies",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_GameLobbyMembers_UserId",
                table: "GameLobbyMembers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameLobbyMembers");

            migrationBuilder.DropTable(
                name: "GameLobbies");
        }
    }
}
