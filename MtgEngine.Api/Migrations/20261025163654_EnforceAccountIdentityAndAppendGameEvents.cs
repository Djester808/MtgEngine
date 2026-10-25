using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class EnforceAccountIdentityAndAppendGameEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "Users",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Users",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 256);

            migrationBuilder.CreateTable(
                name: "PersistedGameEvents",
                columns: table => new
                {
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersistedGameEvents", x => new { x.GameId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_PersistedGameEvents_PersistedGames_GameId",
                        column: x => x.GameId,
                        principalTable: "PersistedGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Flatten appended batches before removing the table so rollback preserves games.
            migrationBuilder.Sql("""
                UPDATE PersistedGames
                SET LogJson = '[' || trim(substr(LogJson, 2, length(LogJson) - 2) || ',' ||
                    (SELECT group_concat(Fragment, ',') FROM
                        (SELECT substr(Json, 2, length(Json) - 2) AS Fragment
                         FROM PersistedGameEvents WHERE GameId = PersistedGames.Id ORDER BY Sequence)), ',') || ']'
                WHERE EXISTS (SELECT 1 FROM PersistedGameEvents WHERE GameId = PersistedGames.Id);
                """);
            migrationBuilder.DropTable(
                name: "PersistedGameEvents");

            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "Users",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldCollation: "NOCASE");

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Users",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 256,
                oldCollation: "NOCASE");
        }
    }
}
