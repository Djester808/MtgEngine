using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgEngine.Api.Migrations
{
    /// <summary>
    /// Collapses "owned, printing unspecified" rows into the pinned row for the same card.
    /// </summary>
    /// <remarks>
    /// An unpinned row (ScryfallId NULL) and a pinned one are not two printings — they are
    /// one card recorded twice — but they occupied separate rows and so rendered as two
    /// tiles with the card's count split between them. The add path now adopts an unpinned
    /// row rather than adding a sibling; this repairs the pairs already stored.
    /// Collections holding several distinct printings keep them: only the *unpinned* row
    /// is folded, into the earliest pinned row of that card and board.
    /// </remarks>
    public partial class CollapsePinnedUnpinnedPairs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add the unpinned row's copies to the earliest pinned row of the same card.
            migrationBuilder.Sql("""
                UPDATE CollectionCards AS keep
                   SET Quantity = keep.Quantity + COALESCE((
                           SELECT u.Quantity FROM CollectionCards u
                            WHERE u.CollectionId = keep.CollectionId
                              AND u.OracleId     = keep.OracleId
                              AND u.Board        = keep.Board
                              AND u.ScryfallId IS NULL), 0),
                       QuantityFoil = keep.QuantityFoil + COALESCE((
                           SELECT u.QuantityFoil FROM CollectionCards u
                            WHERE u.CollectionId = keep.CollectionId
                              AND u.OracleId     = keep.OracleId
                              AND u.Board        = keep.Board
                              AND u.ScryfallId IS NULL), 0)
                 WHERE keep.ScryfallId IS NOT NULL
                   AND EXISTS (SELECT 1 FROM CollectionCards u
                                WHERE u.CollectionId = keep.CollectionId
                                  AND u.OracleId     = keep.OracleId
                                  AND u.Board        = keep.Board
                                  AND u.ScryfallId IS NULL)
                   AND keep.Id = (
                           SELECT p.Id FROM CollectionCards p
                            WHERE p.CollectionId = keep.CollectionId
                              AND p.OracleId     = keep.OracleId
                              AND p.Board        = keep.Board
                              AND p.ScryfallId IS NOT NULL
                            ORDER BY p.AddedAt, p.Id
                            LIMIT 1);
                """);

            // 2. The acquisition data describes the oldest copy in the surviving row, so
            //    carry the unpinned row's back when it was the earlier of the two.
            migrationBuilder.Sql("""
                UPDATE CollectionCards AS keep
                   SET AddedAt = (
                           SELECT u.AddedAt FROM CollectionCards u
                            WHERE u.CollectionId = keep.CollectionId
                              AND u.OracleId     = keep.OracleId
                              AND u.Board        = keep.Board
                              AND u.ScryfallId IS NULL),
                       PriceUsdAtAdd = (
                           SELECT u.PriceUsdAtAdd FROM CollectionCards u
                            WHERE u.CollectionId = keep.CollectionId
                              AND u.OracleId     = keep.OracleId
                              AND u.Board        = keep.Board
                              AND u.ScryfallId IS NULL),
                       PriceUsdFoilAtAdd = (
                           SELECT u.PriceUsdFoilAtAdd FROM CollectionCards u
                            WHERE u.CollectionId = keep.CollectionId
                              AND u.OracleId     = keep.OracleId
                              AND u.Board        = keep.Board
                              AND u.ScryfallId IS NULL)
                 WHERE keep.ScryfallId IS NOT NULL
                   AND EXISTS (SELECT 1 FROM CollectionCards u
                                WHERE u.CollectionId = keep.CollectionId
                                  AND u.OracleId     = keep.OracleId
                                  AND u.Board        = keep.Board
                                  AND u.ScryfallId IS NULL
                                  AND u.AddedAt < keep.AddedAt);
                """);

            // 3. Drop the now-absorbed unpinned rows.
            migrationBuilder.Sql("""
                DELETE FROM CollectionCards
                 WHERE ScryfallId IS NULL
                   AND EXISTS (SELECT 1 FROM CollectionCards p
                                WHERE p.CollectionId = CollectionCards.CollectionId
                                  AND p.OracleId     = CollectionCards.OracleId
                                  AND p.Board        = CollectionCards.Board
                                  AND p.ScryfallId IS NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible: the two rows are indistinguishable once summed into one.
        }
    }
}
