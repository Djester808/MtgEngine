using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The player half of CR 704.5 read against CR 613.10: what a player's own limits forbid the
/// state-based actions to do to them. "Can't" beats "does" (CR 101.2), so a player an effect
/// says can't lose the game does not lose for 0 life (704.5a), for a draw from an empty library
/// (704.5b) or for poison (704.5c) while that effect stands — and the two are not the same
/// afterwards, which is the point of the second test here.
/// <para>
/// The limits come from <see cref="FakeCharacteristics.Limits"/> rather than from a permanent:
/// which continuous effect puts a limit on a player is the layer system's question (CR 613.10),
/// and a state-based action's question is only what the limit then forbids. The same file holds
/// the delegation test for CR 702.5b, because the player filter is now the query's to answer.
/// </para>
/// </summary>
public sealed partial class StateBasedActionTests
{
    private static PlayerId Seat(int player) => new(player);

    /// <summary>
    /// CR 704.5a with 101.2 and 104.3: the player is at 0 life and stays in the game, and 704.5a
    /// reads the state rather than a moment that has passed — so the loss happens on the next
    /// check after the limit ends, with nothing needed to re-propose it.
    /// </summary>
    [Fact]
    [RuleRef("704.5a")]
    public void A_player_who_cant_lose_survives_zero_life_and_loses_when_the_limit_ends()
    {
        var board = new Board(players: 3).Life(1, 0);
        characteristics.Limits[Seat(1)] = new PlayerLimits(PlayerRestriction.Lose);

        var protectedCheck = sba.Check(board.State);

        Assert.Empty(protectedCheck.Events);
        Assert.Same(board.State, protectedCheck.State);
        Assert.False(protectedCheck.State.Player(Seat(1)).HasLost);
        Assert.Equal(0, protectedCheck.State.Player(Seat(1)).Life);

        characteristics.Ends(Seat(1));

        var after = sba.Check(protectedCheck.State);

        var lost = Assert.Single(after.Events.OfType<PlayerLost>());
        Assert.Equal(Seat(1), lost.Player);
        Assert.Contains("life", lost.Reason, StringComparison.Ordinal);
        Assert.True(after.State.Player(Seat(1)).HasLost);
    }

    /// <summary>
    /// The <see cref="PlayerRestriction.Lose"/> flag and the <c>CantWin</c> half of
    /// <see cref="PlayerLimits"/> are different prohibitions: only the first one is 704.5a's, and
    /// a player who merely can't win still loses at 0 life. Without this the two fields would be
    /// indistinguishable in the rule that reads them.
    /// </summary>
    [Fact]
    [RuleRef("704.5a")]
    public void A_player_who_only_cant_win_still_loses_at_zero_life()
    {
        var board = new Board(players: 3).Life(1, 0);
        characteristics.Limits[Seat(1)] = new PlayerLimits(CantWin: true);

        Assert.True(sba.Check(board.State).State.Player(Seat(1)).HasLost);
    }

    /// <summary>
    /// CR 704.5b with 121.4 and 101.2. Unlike 704.5a this one does not read the state: it asks
    /// whether a draw was attempted "since the last time state-based actions were checked", so
    /// the flag is consumed by the check that read it whether or not the loss happened. A player
    /// who could not lose therefore survives the draw for good — the clock has been reset, and
    /// the limit ending does not bring the loss back.
    /// </summary>
    [Fact]
    [RuleRef("704.5b")]
    public void A_player_who_cant_lose_survives_an_empty_library_draw_and_the_flag_is_still_consumed()
    {
        var board = new Board(players: 3).DrewFromEmptyLibrary(1);
        characteristics.Limits[Seat(1)] = new PlayerLimits(PlayerRestriction.Lose);

        var protectedCheck = sba.Check(board.State);

        Assert.DoesNotContain(protectedCheck.Events, e => e is PlayerLost);
        Assert.False(protectedCheck.State.Player(Seat(1)).HasLost);

        var cleared = Assert.Single(protectedCheck.Events.OfType<PlayerFlagCleared>());
        Assert.Equal(Seat(1), cleared.Player);
        Assert.False(protectedCheck.State.Player(Seat(1)).DrewFromEmptyLibrary);

        characteristics.Ends(Seat(1));

        var after = sba.Check(protectedCheck.State);

        Assert.Empty(after.Events);
        Assert.False(after.State.Player(Seat(1)).HasLost);
    }

    /// <summary>
    /// CR 704.5c with 101.2: the third loss a player's own state decides. Poison is a counter and
    /// not a moment, so this behaves like 704.5a — the loss lands on the first check after the
    /// limit ends.
    /// </summary>
    [Fact]
    [RuleRef("704.5c")]
    public void A_player_who_cant_lose_survives_ten_poison_counters_until_the_limit_ends()
    {
        var board = new Board(players: 3).Poison(1, 10);
        characteristics.Limits[Seat(1)] = new PlayerLimits(PlayerRestriction.Lose);

        Assert.Empty(sba.Check(board.State).Events);

        characteristics.Ends(Seat(1));

        var lost = Assert.Single(sba.Check(board.State).Events.OfType<PlayerLost>());
        Assert.Contains("poison", lost.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A limit is per player, not a switch on the table: the unrestricted seat at 0 life loses in
    /// the same check that leaves the restricted one alone, and 704.3 performs both from the one
    /// snapshot.
    /// </summary>
    [Fact]
    [RuleRef("704.3")]
    public void A_limit_on_one_player_does_not_save_another()
    {
        var board = new Board(players: 3).Life(1, 0).Life(2, 0);
        characteristics.Limits[Seat(1)] = new PlayerLimits(PlayerRestriction.Lose);

        var outcome = sba.Check(board.State);

        var lost = Assert.Single(outcome.Events.OfType<PlayerLost>());
        Assert.Equal(Seat(2), lost.Player);
        Assert.False(outcome.State.Player(Seat(1)).HasLost);
    }

    /// <summary>
    /// CR 702.5b: 704.5m asks the query whether the enchanted player answers the Aura's filter,
    /// and no longer answers it itself. The fake records what it was asked, so the delegation is
    /// asserted directly rather than inferred from a verdict a private copy could also reach.
    /// </summary>
    [Fact]
    [RuleRef("702.5b")]
    public void The_enchant_player_filter_is_asked_of_the_query()
    {
        var filter = new PlayerFilter(Opponent: true);
        var board = new Board();
        board.Put(PlayerAura(filter), attachedToPlayer: 1);

        sba.Check(board.State);

        var asked = Assert.Single(query.PlayersAsked);
        Assert.Same(filter, asked.Filter);
        Assert.Equal(Seat(1), asked.Candidate);
    }

    /// <summary>
    /// The other half of the delegation: an enchant ability with no <c>OfPlayer</c> is not a
    /// question for the query at all (E5c's reading — neither half named is "enchant anything"),
    /// so nothing is asked and the Aura stays. A rule that asked anyway would fail here against
    /// the real query, whose answer to an empty filter is "everyone".
    /// </summary>
    [Fact]
    [RuleRef("702.5b")]
    public void An_enchant_with_no_player_filter_asks_the_query_nothing()
    {
        var board = new Board();
        board.Put(PlayerAura(), attachedToPlayer: 1);

        Assert.Empty(sba.Check(board.State).Events);
        Assert.Empty(query.PlayersAsked);
    }

    /// <summary>
    /// CR 702.16d with 702.16j and 704.5m: a player with protection from everything can't be
    /// enchanted, and protection from everything is protection from each object (CR 702.16b), so
    /// no Aura is a legal one — not an "enchant player" Aura, not a bare one, and not one they
    /// control themselves. The query is never asked: the limit answers before the filter does.
    /// </summary>
    [Fact]
    [RuleRef("702.16d")]
    public void An_aura_on_a_player_with_protection_from_everything_is_put_into_its_owners_graveyard()
    {
        var board = new Board();
        board.Put(PlayerAura(new PlayerFilter()), attachedToPlayer: 1);
        characteristics.Limits[Seat(1)] = new PlayerLimits(ProtectionFromEverything: true);

        Assert.Equal(Zone.Graveyard, Zoned(sba.Check(board.State).State, "sba curse"));
        Assert.Empty(query.PlayersAsked);
    }

    /// <summary>
    /// The same Aura on the same board without the limit stays, so the test above is about the
    /// protection and not about the filter it carries.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void The_same_aura_stays_on_a_player_with_no_protection()
    {
        var board = new Board();
        board.Put(PlayerAura(new PlayerFilter()), attachedToPlayer: 1);

        Assert.Empty(sba.Check(board.State).Events);
    }
}
