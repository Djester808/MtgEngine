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
        if (Destination(board, search, card) is null)
            return [];

        var wanted = search.UpTo is Fixed { N: > 0 and var n } ? n : 1;
        var stocked = new List<string>(wanted);
        for (var i = 0; i < wanted && board.CanStock; i++)
            stocked.Add(board.Stocks(card));
        return stocked;
    }

    /// <summary>
    /// CR 701.23a: where the card the library was stocked with goes, and whether it arrives tapped.
    /// A search with no <see cref="Search.Instead"/> puts every card it found into its own
    /// <see cref="Search.To"/>. One that carries them reads the card found against each entry's
    /// filter in the order they are written and the first entry that admits it wins — the reading
    /// the interpreter makes, and the reason "put it into your hand; if it's a land card, put it
    /// onto the battlefield tapped instead" ends with a land on the battlefield and not in a hand.
    /// Null when an entry names something the board cannot read against a card: the search then
    /// gets no scenario at all, because a guessed zone would be reported as the card failing.
    /// </summary>
    [RuleRef("701.23a")]
    private static (Zone To, bool Tapped)? Destination(SmokeBoard board, Search search, string card)
    {
        foreach (var instead in search.Instead ?? [])
        {
            switch (SmokeFillers.Admits(instead.Matching, card, board.AnnouncedX))
            {
                case null:
                    return null;
                case true:
                    return (instead.To, instead.Tapped);
                default:
                    break;
            }
        }

        return (search.To, search.Tapped);
    }

    /// <summary>
    /// Where each found card ended up, and CR 701.20's shuffle. A search that puts the cards back
    /// on top of the library moves nothing, so the shuffle is all there is to see — and it is worth
    /// seeing, because a search that never shuffled would leave the library stacked. The shuffle is
    /// asserted as a shuffle and not as "something was reordered", which the backlog also logs. A
    /// card that arrives on the battlefield is asserted tapped or untapped as CR 701.23a says,
    /// because "put it onto the battlefield tapped" is half of what those searches promise.
    /// </summary>
    [RuleRef("701.20")]
    private static void Found(SmokeBoard board, Search search, IReadOnlyList<string> found)
    {
        if (SmokeFillers.Findable(search.For, board.AnnouncedX) is not { } stocked
            || Destination(board, search, stocked) is not { } went)
            return;

        var n = found.Count;
        switch (went.To)
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

        // CR 701.9b: a random discard later in the same resolution may take the card the search
        // just put in the hand, so the card found is not named — the counts above are what holds
        // whichever card the game took, and an expectation naming this one would pass or fail on
        // the shuffle. A card the search put anywhere else is out of a discard's reach.
        if (went.To is not Zone.Library && !(went.To is Zone.Hand && board.DiscardsAtRandom))
        {
            foreach (var card in found)
            {
                board.Expect(new InZone(card, went.To));
                if (went.To is Zone.Battlefield)
                    board.Expect(new TappedIs(card, went.Tapped));
            }
        }

        if (search.ShuffleAfter)
            board.Expect(new Shuffled(Caster));
    }
}
