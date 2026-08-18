using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class UserProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AvatarUpdatedAt",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Bio",
                table: "Users",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "Users",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FavoriteCommanderOracleId",
                table: "Users",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FavoriteFormat",
                table: "Users",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProfileUpdatedAt",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tagline",
                table: "Users",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserAvatars",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    ETag = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAvatars", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserAvatars_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserAvatars");

            migrationBuilder.DropColumn(
                name: "AvatarUpdatedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Bio",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FavoriteCommanderOracleId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FavoriteFormat",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ProfileUpdatedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Tagline",
                table: "Users");
        }
    }
}
