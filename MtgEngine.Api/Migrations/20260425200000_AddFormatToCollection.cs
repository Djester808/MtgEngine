using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFormatToCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "Collections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommanderOracleId",
                table: "Collections",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Format",            table: "Collections");
            migrationBuilder.DropColumn(name: "CommanderOracleId", table: "Collections");
        }
    }
}
