using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class UpdateBoardIndexAndDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CollectionCards_CollectionId_ScryfallId",
                table: "CollectionCards");

            migrationBuilder.AlterColumn<string>(
                name: "Board",
                table: "CollectionCards",
                type: "TEXT",
                nullable: false,
                defaultValue: "main",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionCards_CollectionId_ScryfallId_Board",
                table: "CollectionCards",
                columns: new[] { "CollectionId", "ScryfallId", "Board" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CollectionCards_CollectionId_ScryfallId_Board",
                table: "CollectionCards");

            migrationBuilder.AlterColumn<string>(
                name: "Board",
                table: "CollectionCards",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "main");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionCards_CollectionId_ScryfallId",
                table: "CollectionCards",
                columns: new[] { "CollectionId", "ScryfallId" },
                unique: true);
        }
    }
}
