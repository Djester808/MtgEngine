using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class PerPrintingCollectionEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CollectionCards_CollectionId_OracleId",
                table: "CollectionCards");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionCards_CollectionId_OracleId",
                table: "CollectionCards",
                columns: new[] { "CollectionId", "OracleId" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionCards_CollectionId_ScryfallId",
                table: "CollectionCards",
                columns: new[] { "CollectionId", "ScryfallId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CollectionCards_CollectionId_OracleId",
                table: "CollectionCards");

            migrationBuilder.DropIndex(
                name: "IX_CollectionCards_CollectionId_ScryfallId",
                table: "CollectionCards");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionCards_CollectionId_OracleId",
                table: "CollectionCards",
                columns: new[] { "CollectionId", "OracleId" },
                unique: true);
        }
    }
}
