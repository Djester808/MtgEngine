namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>A search: the library is stocked with what it is going to find, and the scenario says where each card went.</summary>
public static partial class SmokeScenario
{
    /// <summary>
    /// CR 701.19: the library is stocked with the cards this search is going to find, on top, and
    /// the aliases they were stocked under are returned so the expectations can name them. A search
    /// for "up to N" finds N, because the runner answers with as many as the question allows — so
    /// the board stocks N and the scenario says where every one of them went, rather than asserting
    /// one card and staying silent about the rest. Empty when the search wants a card the runner
    /// has not got, or the library has no room left for them.
    /// </summary>
    [RuleRef("701.19")]
    private static IReadOnlyList<string> Stock(SmokeBoard board, Search search)
    {
        if (search.To is not (Zone.Hand or Zone.Graveyard or Zone.Battlefield or Zone.Library))
            return [];
        if (SmokeFillers.Findable(search.For, board.AnnouncedX) is not { } card)
            return [];

        var wanted = search.UpTo is Fixed { N: > 0 and var n } ? n : 1;
        var stocked = new List<string>(wanted);
        for (var i = 0; i < wanted && board.CanStock; i++)
            stocked.Add(board.Stocks(card));
        return stocked;
    }

    /// <summary>
    /// Where each found card ended up, and CR 701.20's shuffle. A search that puts the cards back
    /// on top of the library moves nothing, so the shuffle is all there is to see — and it is worth
    /// seeing, because a search that never shuffled would leave the library stacked. The shuffle is
    /// asserted as a shuffle and not as "something was reordered", which the backlog also logs.
    /// </summary>
    [RuleRef("701.20")]
    private static void Found(SmokeBoard board, Search search, IReadOnlyList<string> found)
    {
        var n = found.Count;
        switch (search.To)
        {
            case Zone.Hand:
                board.Caster(hand: n, library: -n);
                break;
            case Zone.Graveyard:
                board.Caster(graveyard: n, library: -n);
                break;
            case Zone.Battlefield:
                board.Caster(library: -n);
                board.Arrives(n);
                break;
            default:
                break;
        }

        if (search.To is not Zone.Library)
        {
            foreach (var card in found)
                board.Expect(new InZone(card, search.To));
        }

        if (search.ShuffleAfter)
            board.Expect(new Shuffled(Caster));
    }
}
