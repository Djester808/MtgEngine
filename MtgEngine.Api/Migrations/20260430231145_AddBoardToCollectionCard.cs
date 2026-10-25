using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBoardToCollectionCard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The preceding migration has no Board column. Add it and backfill existing
            // collection rows; altering it relied on SQLite accepting a missing quoted
            // column as a string literal, which current SQLite correctly rejects.
            migrationBuilder.AddColumn<string>(
                name: "Board",
                table: "CollectionCards",
                type: "TEXT",
                nullable: false,
                defaultValue: "main");

            // Backfill uses main, but this historical target model had no default;
            // UpdateBoardIndexAndDefault adds the permanent default in the next step.
            migrationBuilder.AlterColumn<string>(
                name: "Board",
                table: "CollectionCards",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "main");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Board", table: "CollectionCards");
        }
    }
}
