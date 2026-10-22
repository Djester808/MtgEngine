using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class CollectionMetadataAndUndo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CollectionCards_CollectionId_ScryfallId_Board",
                table: "CollectionCards");

            migrationBuilder.DropIndex(
                name: "IX_CollectionCards_Unpinned_Unique",
                table: "CollectionCards");

            migrationBuilder.AddColumn<string>(
                name: "Condition",
                table: "CollectionCards",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "CollectionCards",
                type: "TEXT",
                maxLength: 8,
                nullable: false,
                defaultValue: "en");

            migrationBuilder.AddColumn<string>(
                name: "PurchaseCurrency",
                table: "CollectionCards",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PurchasePrice",
                table: "CollectionCards",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TagsJson",
                table: "CollectionCards",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "CollectionUndos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    CollectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: false),
                    AfterHash = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionUndos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionCards_CollectionId_ScryfallId_Board_Language",
                table: "CollectionCards",
                columns: new[] { "CollectionId", "ScryfallId", "Board", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CollectionCards_Unpinned_Unique",
                table: "CollectionCards",
                columns: new[] { "CollectionId", "OracleId", "Board", "Language" },
                unique: true,
                filter: "\"ScryfallId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionUndos_UserId_ExpiresAt",
                table: "CollectionUndos",
                columns: new[] { "UserId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectionUndos");

            migrationBuilder.DropIndex(
                name: "IX_CollectionCards_CollectionId_ScryfallId_Board_Language",
                table: "CollectionCards");

            migrationBuilder.DropIndex(
                name: "IX_CollectionCards_Unpinned_Unique",
                table: "CollectionCards");

            migrationBuilder.DropColumn(
                name: "Condition",
                table: "CollectionCards");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "CollectionCards");

            migrationBuilder.DropColumn(
                name: "PurchaseCurrency",
                table: "CollectionCards");

            migrationBuilder.DropColumn(
                name: "PurchasePrice",
                table: "CollectionCards");

            migrationBuilder.DropColumn(
                name: "TagsJson",
                table: "CollectionCards");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionCards_CollectionId_ScryfallId_Board",
                table: "CollectionCards",
                columns: new[] { "CollectionId", "ScryfallId", "Board" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CollectionCards_Unpinned_Unique",
                table: "CollectionCards",
                columns: new[] { "CollectionId", "OracleId", "Board" },
                unique: true,
                filter: "\"ScryfallId\" IS NULL");
        }
    }
}
