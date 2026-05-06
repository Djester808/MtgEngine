using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class FixBoardDefaultValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EF Core's SQLite table-rebuild used the column name "Board" as the literal
            // string when copying existing rows into the new schema. Fix those rows.
            migrationBuilder.Sql(
                "UPDATE \"CollectionCards\" SET \"Board\" = 'main' WHERE \"Board\" NOT IN ('main', 'side', 'maybe');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
