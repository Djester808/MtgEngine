using System.Text.Json;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What a player is allowed to know (CR 400.2).
/// </summary>
/// <remarks>
/// The engine this replaces broadcast one state object to a SignalR group containing every
/// player in the game, which handed each of them the other's hand and both libraries. These
/// tests assert the fix negatively — that the secret is <em>absent</em> from what gets sent,
/// not merely marked — because a flag only protects the caller who remembers to read it.
/// </remarks>
public sealed class HiddenInformationTests
{
    [Fact]
    public void A_player_sees_their_own_hand()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        game.Draw(alice);
        game.Draw(alice);

        var view = game.ViewFor(alice);
        var self = view.Players.Single(p => p.PlayerId == alice);

        Assert.NotNull(self.Hand);
        Assert.Equal(2, self.Hand.Count);
    }

    [Fact]
    public void A_player_does_not_see_another_players_hand()
    {
        // CR 402.3: a player can't look at the cards in another player's hand but may count them.
        var (game, alice, bob) = TestCards.TwoPlayer();
        game.Draw(bob);
        game.Draw(bob);

        var view = game.ViewFor(alice);
        var opponent = view.Players.Single(p => p.PlayerId == bob);

        Assert.Null(opponent.Hand);
        Assert.Equal(2, opponent.HandCount);
    }

    [Fact]
    public void Nobody_sees_a_library_including_their_own()
    {
        // CR 401.2: players can't look at or change the order of cards in a library. A view with
        // the owner's library in it would be a cheat available to that player's own client.
        var (game, alice, _) = TestCards.TwoPlayer(deckSize: 7);

        var view = game.ViewFor(alice);
        var self = view.Players.Single(p => p.PlayerId == alice);

        Assert.Equal(7, self.LibraryCount);
        Assert.DoesNotContain(nameof(PlayerState.Library), PropertyNames(typeof(PlayerView)));
    }

    [Fact]
    public void The_serialised_view_carries_no_hidden_card_name()
    {
        // The assertion that actually matters: whatever the shape of the record, the bytes on
        // the wire must not contain a card only the other player may see. Card names are unique
        // per deck here so a leak is findable by search.
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 12);
        game.Draw(bob);
        game.Draw(bob);
        game.Draw(alice);

        var json = JsonSerializer.Serialize(game.ViewFor(alice));

        var bobsHand = game.State.GetPlayer(bob).Hand
            .Select(id => game.State.GetObject(id).Card.Name);
        foreach (var name in bobsHand)
            Assert.DoesNotContain(name, json, StringComparison.Ordinal);

        foreach (var id in game.State.GetPlayer(alice).Library)
            Assert.DoesNotContain(
                game.State.GetObject(id).Card.Name, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_zones_are_visible_to_everyone()
    {
        // CR 400.2: graveyard, battlefield, stack, exile and command are public.
        var (game, alice, bob) = TestCards.TwoPlayer();
        var drawn = game.Draw(bob)!.Value;
        var permanent = game.Move(drawn, Zone.Battlefield, MoveCause.Play);
        game.Move(permanent, Zone.Graveyard, MoveCause.Destroy);

        var view = game.ViewFor(alice);

        Assert.Single(view.Players.Single(p => p.PlayerId == bob).Graveyard);
    }

    [Fact]
    public void A_view_can_only_be_built_for_someone_in_the_game()
    {
        var (game, _, _) = TestCards.TwoPlayer();

        Assert.Throws<InvalidOperationException>(() => game.ViewFor(Guid.NewGuid()));
    }

    [Fact]
    public void The_view_reports_printed_characteristics_under_that_name()
    {
        // Current power and toughness are the printed values with continuous effects layered
        // over them (CR 613), which does not exist yet. A field called Power would be a lie the
        // moment the first lord is implemented, so the view says what it actually knows.
        var (game, alice, _) = TestCards.TwoPlayer();
        var permanent = game.Move(
            game.State.GetPlayer(alice).Library[0], Zone.Battlefield, MoveCause.Play);

        var shown = game.ViewFor(alice).Battlefield.Single(o => o.Id == permanent.Value);

        Assert.Equal(2, shown.PrintedPower);
        Assert.Equal("Creature — Bear", shown.TypeLine);
        Assert.False(shown.IsTapped);
    }

    [Fact]
    public void Everyone_is_told_the_game_is_waiting_and_on_whom()
    {
        // A board that simply stops with no explanation is the worst thing a client can show,
        // so the fact of the question and who owns it are public.
        var (game, alice, bob) = TestCards.TwoPlayer();
        game.BeginPlay();

        var theirs = game.ViewFor(bob).Choice;
        Assert.NotNull(theirs);
        Assert.Equal(alice, theirs.PlayerId);
        Assert.Equal("Mulligan", theirs.Kind);
    }

    [Fact]
    public void Only_the_player_being_asked_sees_the_options()
    {
        // The options can be hidden information: bottoming after a mulligan lists that player's
        // hand, so sending it to the table would hand the opponent seven cards.
        var (game, alice, bob) = TestCards.TwoPlayer();
        game.BeginPlay();
        game.Choose(alice, ["mulligan"]);
        game.Choose(bob, ["keep"]);
        game.Choose(alice, ["keep"]);

        var mine = game.ViewFor(alice).Choice;
        var theirs = game.ViewFor(bob).Choice;

        Assert.Equal("BottomAfterMulligan", mine!.Kind);
        Assert.NotNull(mine.Options);
        Assert.Equal(7, mine.Options.Count);
        Assert.Null(theirs!.Options);
    }

    [Fact]
    public void The_serialised_view_of_a_bottoming_choice_names_no_card_to_the_opponent()
    {
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 12);
        game.BeginPlay();
        game.Choose(alice, ["mulligan"]);
        game.Choose(bob, ["keep"]);
        game.Choose(alice, ["keep"]);

        var json = JsonSerializer.Serialize(game.ViewFor(bob));

        foreach (var id in game.State.GetPlayer(alice).Hand)
            Assert.DoesNotContain(game.State.GetObject(id).Card.Name, json, StringComparison.Ordinal);
    }

    private static IEnumerable<string> PropertyNames(Type type) =>
        type.GetProperties().Select(p => p.Name);
}
