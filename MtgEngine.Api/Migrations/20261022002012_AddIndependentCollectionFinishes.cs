using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddIndependentCollectionFinishes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FinishMetadataJson",
                table: "CollectionCards",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<decimal>(
                name: "PriceUsdEtchedAtAdd",
                table: "CollectionCards",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuantityEtched",
                table: "CollectionCards",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QuantityEtchedAfter",
                table: "CollectionCardEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QuantityEtchedDelta",
                table: "CollectionCardEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinishMetadataJson",
                table: "CollectionCards");

            migrationBuilder.DropColumn(
                name: "PriceUsdEtchedAtAdd",
                table: "CollectionCards");

            migrationBuilder.DropColumn(
                name: "QuantityEtched",
                table: "CollectionCards");

            migrationBuilder.DropColumn(
                name: "QuantityEtchedAfter",
                table: "CollectionCardEvents");

            migrationBuilder.DropColumn(
                name: "QuantityEtchedDelta",
                table: "CollectionCardEvents");
        }
    }
}
