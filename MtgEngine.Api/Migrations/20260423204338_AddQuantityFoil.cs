using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddQuantityFoil : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsFoil",
                table: "CollectionCards",
                newName: "QuantityFoil");

            // IsFoil was stored as 0/1; for rows that were fully foil,
            // move Quantity into QuantityFoil and zero out Quantity.
            migrationBuilder.Sql(
                "UPDATE CollectionCards SET QuantityFoil = Quantity, Quantity = 0 WHERE QuantityFoil = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "QuantityFoil",
                table: "CollectionCards",
                newName: "IsFoil");
        }
    }
}
