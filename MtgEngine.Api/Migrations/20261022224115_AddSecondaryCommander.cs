using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSecondaryCommander : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecondaryCommanderOracleId",
                table: "Collections",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecondaryCommanderOracleId",
                table: "Collections");
        }
    }
}
