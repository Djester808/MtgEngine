using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class LifeCounterMatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LifeMatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartingLife = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifeMatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LifeMatchSeats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Seat = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Won = table.Column<bool>(type: "INTEGER", nullable: false),
                    LossReason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FinalLife = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifeMatchSeats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LifeMatchSeats_LifeMatches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "LifeMatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LifeMatches_RecordedAt",
                table: "LifeMatches",
                column: "RecordedAt");

            migrationBuilder.CreateIndex(
                name: "IX_LifeMatchSeats_MatchId_Seat",
                table: "LifeMatchSeats",
                columns: new[] { "MatchId", "Seat" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LifeMatchSeats_MatchId_UserId",
                table: "LifeMatchSeats",
                columns: new[] { "MatchId", "UserId" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LifeMatchSeats_UserId",
                table: "LifeMatchSeats",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LifeMatchSeats");

            migrationBuilder.DropTable(
                name: "LifeMatches");
        }
    }
}
