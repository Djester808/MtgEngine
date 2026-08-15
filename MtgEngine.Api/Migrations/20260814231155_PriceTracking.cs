using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class PriceTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PriceUsdAtAdd",
                table: "CollectionCards",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PriceUsdFoilAtAdd",
                table: "CollectionCards",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CardPriceSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScryfallId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Usd = table.Column<decimal>(type: "TEXT", nullable: true),
                    UsdFoil = table.Column<decimal>(type: "TEXT", nullable: true),
                    UsdEtched = table.Column<decimal>(type: "TEXT", nullable: true),
                    Eur = table.Column<decimal>(type: "TEXT", nullable: true),
                    EurFoil = table.Column<decimal>(type: "TEXT", nullable: true),
                    Tix = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardPriceSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CardPriceSnapshots_CapturedAt",
                table: "CardPriceSnapshots",
                column: "CapturedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CardPriceSnapshots_ScryfallId_CapturedAt",
                table: "CardPriceSnapshots",
                columns: new[] { "ScryfallId", "CapturedAt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CardPriceSnapshots");

            migrationBuilder.DropColumn(
                name: "PriceUsdAtAdd",
                table: "CollectionCards");

            migrationBuilder.DropColumn(
                name: "PriceUsdFoilAtAdd",
                table: "CollectionCards");
        }
    }
}
