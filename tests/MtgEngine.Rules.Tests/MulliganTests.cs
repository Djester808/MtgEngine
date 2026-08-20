using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The mulligan procedure (CR 103.5).
/// </summary>
/// <remarks>
/// Left out of the first pass with a note saying it "needs the choice machinery", and then not
/// picked up when that machinery was built. It is the first decision of every game, so a game
/// that skipped it was not a game anyone would recognise.
/// </remarks>
public sealed class MulliganTests
{
    private static (Game Game, Guid Alice, Guid Bob) Dealt(int deckSize = 40)
    {
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize);
        game.BeginPlay();
        return (game, alice, bob);
    }

    private static void Keep(Game game) =>
        game.Choose(game.State.Choice!.PlayerId, ["keep"]);

    private static void Mulligan(Game game) =>
        game.Choose(game.State.Choice!.PlayerId, ["mulligan"]);

    [Fact]
    public void The_starting_player_declares_first()
    {
        // CR 103.5: "First, the starting player declares whether they will take a mulligan.
        // Then each other player in turn order does the same."
        var (game, alice, _) = Dealt();

        Assert.Equal(ChoiceKind.Mulligan, game.State.Choice!.Kind);
        Assert.Equal(alice, game.State.Choice.PlayerId);
    }

    [Fact]
    public void Each_player_declares_in_turn_order()
    {
        var (game, alice, bob) = Dealt();

        Keep(game);
        Assert.Equal(bob, game.State.Choice!.PlayerId);
    }

    [Fact]
    public void Everyone_keeping_starts_the_game()
    {
        var (game, _, _) = Dealt();

        Keep(game);
        Keep(game);

        Assert.Null(game.State.Choice);
        Assert.False(game.State.IsMulliganing);
        Assert.Equal(1, game.State.TurnNumber);
        Assert.Contains(game.Log, e => e is MulligansFinished);
    }

    [Fact]
    public void A_mulligan_draws_a_fresh_hand_of_the_full_size()
    {
        // CR 103.5: shuffle the hand back, draw a new hand equal to the starting hand size.
        // The cards only go to the bottom once the player finally keeps.
        var (game, alice, _) = Dealt();
        var firstHand = game.State.GetPlayer(alice).Hand
            .Select(id => game.State.GetObject(id).Card.Name).ToList();

        Mulligan(game);
        Keep(game);

        // Alice declares again after the round; she is asked a second time.
        Assert.Equal(alice, game.State.Choice!.PlayerId);
        var secondHand = game.State.GetPlayer(alice).Hand
            .Select(id => game.State.GetObject(id).Card.Name).ToList();

        Assert.Equal(7, secondHand.Count);
        Assert.NotEqual(firstHand, secondHand);
        Assert.Contains(game.Log, e => e is MulliganTaken);
    }

    [Fact]
    public void Keeping_after_one_mulligan_puts_one_card_on_the_bottom()
    {
        // CR 103.5: a player puts a number of cards equal to the number of mulligans taken on
        // the bottom of their library.
        var (game, alice, _) = Dealt();

        Mulligan(game);
        Keep(game);
        Keep(game);

        var bottoming = game.State.Choice;
        Assert.NotNull(bottoming);
        Assert.Equal(ChoiceKind.BottomAfterMulligan, bottoming.Kind);
        Assert.Equal(alice, bottoming.PlayerId);
        Assert.Equal(1, bottoming.MinPicks);
        Assert.Equal(7, bottoming.Options.Count);

        var libraryBefore = game.State.GetPlayer(alice).Library.Count;
        game.Choose(alice, [bottoming.Options[0].Id]);

        Assert.Equal(6, game.State.GetPlayer(alice).Hand.Count);
        Assert.Equal(libraryBefore + 1, game.State.GetPlayer(alice).Library.Count);
        Assert.Equal(1, game.State.TurnNumber);
    }

    [Fact]
    public void Two_mulligans_bottom_two_cards()
    {
        var (game, alice, _) = Dealt();

        Mulligan(game);
        Keep(game);
        Mulligan(game);
        Keep(game);

        Assert.Equal(2, game.State.Choice!.MinPicks);
        game.Choose(alice, [.. game.State.Choice.Options.Take(2).Select(o => o.Id)]);

        Assert.Equal(5, game.State.GetPlayer(alice).Hand.Count);
    }

    [Fact]
    public void The_card_put_on_the_bottom_is_the_one_chosen()
    {
        var (game, alice, _) = Dealt();
        Mulligan(game);
        Keep(game);
        Keep(game);

        var chosen = game.State.Choice!.Options[3];
        game.Choose(alice, [chosen.Id]);

        var hand = game.State.GetPlayer(alice).Hand
            .Select(id => game.State.GetObject(id).Card.Name).ToList();
        Assert.DoesNotContain(chosen.Label, hand);
        Assert.Equal(
            chosen.Label,
            game.State.GetObject(game.State.GetPlayer(alice).Library[^1]).Card.Name);
    }

    [Fact]
    public void A_player_who_keeps_is_not_asked_again()
    {
        // CR 103.5: "Once a player chooses not to take a mulligan... that player may not take
        // any further mulligans." Bob keeps first and is never asked in the second round.
        var (game, alice, bob) = Dealt();

        Mulligan(game);
        Keep(game);

        Assert.Equal(alice, game.State.Choice!.PlayerId);
        Keep(game);

        // Alice's keep ends the round; the only thing left is her bottoming choice.
        Assert.Equal(ChoiceKind.BottomAfterMulligan, game.State.Choice!.Kind);
        Assert.Equal(alice, game.State.Choice.PlayerId);
        Assert.Equal(0, game.State.MulligansTaken.GetValueOrDefault(bob));
    }

    [Fact]
    public void Mulligans_stop_when_the_hand_would_be_empty()
    {
        // CR 103.5: a player can take mulligans until their opening hand would be zero cards.
        var (game, alice, _) = Dealt(deckSize: 60);

        for (var guard = 0; guard < 40 && game.State.Choice?.Kind == ChoiceKind.Mulligan; guard++)
        {
            if (game.State.Choice.PlayerId == alice)
                Mulligan(game);
            else
                Keep(game);
        }

        Assert.Equal(7, game.State.MulligansTaken.GetValueOrDefault(alice));
        Assert.NotEqual(ChoiceKind.Mulligan, game.State.Choice?.Kind);
    }

    [Fact]
    public void A_game_that_mulliganed_still_replays_to_the_same_state()
    {
        var (game, alice, _) = Dealt();
        Mulligan(game);
        Keep(game);
        Keep(game);
        game.Choose(alice, [game.State.Choice!.Options[0].Id]);
        TestCards.PassToStep(game, TurnStep.PrecombatMain);

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }

    [Fact]
    public void A_game_can_still_skip_the_procedure_for_a_test()
    {
        var (game, _, _) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        Assert.Null(game.State.Choice);
        Assert.Equal(1, game.State.TurnNumber);
    }
}
