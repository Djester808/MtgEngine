using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddIsDeckFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDeck",
                table: "Collections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDeck",
                table: "Collections");
        }
    }
}
