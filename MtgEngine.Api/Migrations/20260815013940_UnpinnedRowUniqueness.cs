using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <inheritdoc />
    public partial class UnpinnedRowUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing data violates the new index: rows that pin no printing were never
            // constrained (SQLite treats NULLs as distinct), so one card could occupy
            // several rows in the same collection and board, splitting its count across
            // duplicate tiles. Fold each group into its earliest row before adding the
            // index, or the CREATE UNIQUE INDEX below fails on any affected database.
            migrationBuilder.Sql("""
                UPDATE CollectionCards AS keep
                   SET Quantity = (
                           SELECT SUM(d.Quantity) FROM CollectionCards d
                            WHERE d.CollectionId = keep.CollectionId
                              AND d.OracleId     = keep.OracleId
                              AND d.Board        = keep.Board
                              AND d.ScryfallId IS NULL),
                       QuantityFoil = (
                           SELECT SUM(d.QuantityFoil) FROM CollectionCards d
                            WHERE d.CollectionId = keep.CollectionId
                              AND d.OracleId     = keep.OracleId
                              AND d.Board        = keep.Board
                              AND d.ScryfallId IS NULL)
                 WHERE keep.ScryfallId IS NULL
                   AND keep.Id = (
                           SELECT e.Id FROM CollectionCards e
                            WHERE e.CollectionId = keep.CollectionId
                              AND e.OracleId     = keep.OracleId
                              AND e.Board        = keep.Board
                              AND e.ScryfallId IS NULL
                            ORDER BY e.AddedAt, e.Id
                            LIMIT 1);
                """);

            // Then drop the now-redundant later rows, keeping the earliest of each group
            // (the one that just absorbed the quantities, and whose AddedAt/price-at-add
            // describe the oldest copy).
            // ROW_NUMBER rather than GROUP BY/HAVING: the keeper must be exactly the row
            // the UPDATE above chose, and (AddedAt, Id) reproduces its ORDER BY … LIMIT 1
            // unambiguously.
            migrationBuilder.Sql("""
                DELETE FROM CollectionCards
                 WHERE ScryfallId IS NULL
                   AND Id NOT IN (
                           SELECT Id FROM (
                               SELECT Id,
                                      ROW_NUMBER() OVER (
                                          PARTITION BY CollectionId, OracleId, Board
                                          ORDER BY AddedAt, Id) AS rn
                                 FROM CollectionCards
                                WHERE ScryfallId IS NULL)
                          WHERE rn = 1);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CollectionCards_Unpinned_Unique",
                table: "CollectionCards",
                columns: new[] { "CollectionId", "OracleId", "Board" },
                unique: true,
                filter: "\"ScryfallId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CollectionCards_Unpinned_Unique",
                table: "CollectionCards");
        }
    }
}
