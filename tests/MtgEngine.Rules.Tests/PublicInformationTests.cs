using MtgEngine.Domain.Enums;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What every player is entitled to know, and what the view therefore has to carry.
/// </summary>
/// <remarks>
/// The sibling <see cref="HiddenInformationTests"/> assert that secrets are absent. These
/// assert the other half, which had been quietly failing: two facts that are public in the
/// rules — whose priority it is (CR 117.1) and what mana a player has floating (CR 106.4) —
/// were not in the view at all, so no client could show them. A board that cannot say whose
/// turn it is to act offers its buttons to whoever is looking and lets the server refuse them.
/// </remarks>
public sealed class PublicInformationTests
{
    [Fact]
    public void The_view_says_who_has_priority()
    {
        // CR 117.1: the active player receives priority at the start of most steps.
        var (game, alice, bob) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        var view = game.ViewFor(bob);

        Assert.NotNull(view.PriorityPlayerId);
        Assert.Equal(game.State.Priority.Holder, view.PriorityPlayerId);
        // Both players are told the same thing: priority is not a secret.
        Assert.Equal(game.ViewFor(alice).PriorityPlayerId, view.PriorityPlayerId);
    }

    [Fact]
    public void Nobody_holding_priority_is_reported_as_nobody()
    {
        // CR 502.4: no player receives priority during the untap step. Null rather than a
        // fallback to the active player — "nobody may act" is a real state and a client that
        // is told Alice may act during untap will offer her an action the engine refuses.
        var (game, alice, _) = TestCards.TwoPlayer();

        Assert.Null(game.State.Priority.Holder);
        Assert.Null(game.ViewFor(alice).PriorityPlayerId);
    }

    [Fact]
    public void The_view_carries_a_players_floating_mana()
    {
        // CR 106.4: mana in a pool is public, and it empties as each step ends (CR 500.5), so
        // a player cannot reconstruct it from what they remember tapping.
        var (game, alice, bob) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        var seatState = game.State.GetPlayer(alice);
        var withMana = game.State.WithPlayer(
            seatState with { ManaPool = seatState.ManaPool.Add(ManaColor.Red, 2) });
        var view = ViewOf(withMana, bob);
        var seat = view.Players.Single(p => p.PlayerId == alice);

        Assert.Equal(2, seat.ManaPool["R"]);
        // Kinds the player has none of are absent rather than zero, so a client can render the
        // pool by iterating it.
        Assert.False(seat.ManaPool.ContainsKey("G"));
    }

    [Fact]
    public void Colorless_mana_is_its_own_symbol()
    {
        // CR 106.1b: colourless is a kind of mana, not the absence of a colour.
        var (game, alice, _) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        var seatState = game.State.GetPlayer(alice);
        var withMana = game.State.WithPlayer(
            seatState with { ManaPool = seatState.ManaPool.AddColorless(3) });
        var seat = ViewOf(withMana, alice).Players.Single(p => p.PlayerId == alice);

        Assert.Equal(3, seat.ManaPool["C"]);
        Assert.Single(seat.ManaPool);
    }

    private static Views.GameView ViewOf(GameState state, Guid viewer) =>
        Views.PlayerViewProjector.Project(state, viewer);
}
