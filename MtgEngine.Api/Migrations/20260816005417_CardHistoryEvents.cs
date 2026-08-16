using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class CardHistoryEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CollectionCardEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CollectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CollectionName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsDeck = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    OracleId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ScryfallId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SetCode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Board = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "main"),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    QuantityDelta = table.Column<int>(type: "INTEGER", nullable: false),
                    QuantityFoilDelta = table.Column<int>(type: "INTEGER", nullable: false),
                    QuantityAfter = table.Column<int>(type: "INTEGER", nullable: false),
                    QuantityFoilAfter = table.Column<int>(type: "INTEGER", nullable: false),
                    CounterpartCollectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CounterpartCollectionName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PriceUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionCardEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionCardEvents_UserId_OracleId_CreatedAt",
                table: "CollectionCardEvents",
                columns: new[] { "UserId", "OracleId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectionCardEvents");
        }
    }
}
