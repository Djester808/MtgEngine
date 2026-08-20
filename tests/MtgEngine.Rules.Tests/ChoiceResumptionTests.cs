using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What the game does after a question is answered.
/// </summary>
/// <remarks>
/// A choice interrupts something. Picking it back up correctly is the part most likely to be
/// subtly wrong, because the resumption has to hand priority to whoever was about to get it —
/// not to whoever usually gets it. CR 117.5 is explicit: after state-based actions and triggers,
/// "the player who would have received priority does so".
/// </remarks>
public sealed class ChoiceResumptionTests
{
    private sealed class Dies : IAbilitySource
    {
        public IReadOnlyList<TriggeredAbilityDefinition> TriggersOf(CardDefinition card) =>
            card.OracleId.Contains("watcher", StringComparison.Ordinal)
                ?
                [
                    new TriggeredAbilityDefinition
                    {
                        Id = "dies",
                        Text = "When this dies, draw a card.",
                        Triggers = (e, state, source) =>
                            e is ObjectMoved { From: Zone.Battlefield, To: Zone.Graveyard } m
                            && m.OldId == source.Id,
                        Effects = [new DrawCards(1)],
                    },
                ]
                : [];
    }

    private static (Game Game, Guid Alice, Guid Bob) InMainPhase(IAbilitySource? abilities = null)
    {
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 20, TestCards.Deck(40, "Alice")),
                new PlayerSetup(bob, "Bob", 20, TestCards.Deck(40, "Bob")),
            ],
            new GameRandom(1),
            startingPlayerId: alice,
            abilities: abilities ?? NoAbilities.Instance);

        game.BeginPlay(withMulligans: false);
        TestCards.PassToStep(game, TurnStep.PrecombatMain);
        return (game, alice, bob);
    }

    [Fact]
    public void Answering_a_choice_returns_priority_to_whoever_was_about_to_get_it()
    {
        // Alice passes, so Bob is the player who would receive priority. A state-based action
        // interrupting that must not hand priority back to Alice just because she is active —
        // that silently skips Bob's window to respond.
        var (game, alice, bob) = InMainPhase();
        game.Create(bob, TestCards.Legend("Karn"), Zone.Battlefield);
        game.Create(bob, TestCards.Legend("Karn"), Zone.Battlefield);

        game.PassPriority(alice);

        Assert.NotNull(game.State.Choice);
        game.Choose(bob, [game.State.Choice!.Options[0].Id]);

        Assert.Equal(bob, game.State.Priority.Holder);
    }

    [Fact]
    public void A_choice_at_the_start_of_a_step_gives_the_active_player_priority()
    {
        // The other half of the same rule. The mulligan question is raised before the first turn
        // begins, and once it is answered it is the active player who would receive priority
        // (CR 117.3a) — nobody was mid-pass, so there is no opponent waiting.
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay();

        game.Choose(alice, ["keep"]);
        game.Choose(bob, ["keep"]);

        Assert.Null(game.State.Choice);
        Assert.Equal(alice, game.State.ActivePlayerId);
        Assert.Equal(alice, game.State.Priority.Holder);
    }

    [Fact]
    public void Two_copies_of_one_card_give_two_orderable_triggers()
    {
        // Both carry the same ability id, so an option keyed on that alone produces two picks
        // that cannot be told apart — and an ordering whose options are indistinguishable is
        // not an ordering. It is the commonest case there is: two of the same creature dying.
        var (game, alice, bob) = InMainPhase(new Dies());
        var first = game.Create(bob, TestCards.Watcher("A"), Zone.Battlefield);
        var second = game.Create(bob, TestCards.Watcher("B"), Zone.Battlefield);
        game.Move(first, Zone.Graveyard, MoveCause.Destroy);
        game.Move(second, Zone.Graveyard, MoveCause.Destroy);
        game.PassPriority(alice);

        var options = game.State.Choice!.Options;
        Assert.Equal(2, options.Count);
        Assert.Equal(2, options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Ordering_triggers_returns_priority_to_whoever_was_about_to_get_it()
    {
        var (game, alice, bob) = InMainPhase(new Dies());
        var first = game.Create(bob, TestCards.Watcher("A"), Zone.Battlefield);
        var second = game.Create(bob, TestCards.Watcher("B"), Zone.Battlefield);

        game.Move(first, Zone.Graveyard, MoveCause.Destroy);
        game.Move(second, Zone.Graveyard, MoveCause.Destroy);

        game.PassPriority(alice);

        var choice = game.State.Choice;
        Assert.NotNull(choice);
        Assert.Equal(ChoiceKind.OrderTriggers, choice.Kind);
        game.Choose(bob, [.. choice.Options.Select(o => o.Id)]);

        Assert.Equal(2, game.State.Stack.Count);
        Assert.Equal(bob, game.State.Priority.Holder);
    }

    [Fact]
    public void A_choice_does_not_survive_the_game_ending()
    {
        var (game, alice, bob) = InMainPhase();
        game.ChangeLife(bob, -20);

        game.PassPriority(alice);

        Assert.True(game.State.IsOver);
        Assert.Null(game.State.Choice);
    }

    [Fact]
    public void Nothing_can_be_answered_once_the_game_is_over()
    {
        var (game, alice, bob) = InMainPhase();
        game.ChangeLife(bob, -20);
        game.PassPriority(alice);

        Assert.Throws<InvalidOperationException>(() => game.Choose(alice, ["anything"]));
    }

    [Fact]
    public void An_answer_of_the_wrong_shape_is_refused()
    {
        var (game, alice, _) = InMainPhase();
        game.Create(alice, TestCards.Legend("Karn"), Zone.Battlefield);
        game.Create(alice, TestCards.Legend("Karn"), Zone.Battlefield);
        TestCards.PassUntil(game, () => game.State.Choice is not null);

        Assert.Throws<InvalidOperationException>(() => game.Choose(alice, []));
        Assert.Throws<InvalidOperationException>(() => game.Choose(alice, ["not-an-option"]));
        Assert.Throws<InvalidOperationException>(
            () => game.Choose(alice, [.. game.State.Choice!.Options.Select(o => o.Id)]));

        // Still waiting: a refused answer is not an answer.
        Assert.NotNull(game.State.Choice);
    }

    [Fact]
    public void The_same_option_cannot_be_picked_twice_in_an_ordering()
    {
        var (game, alice, bob) = InMainPhase(new Dies());
        game.Create(bob, TestCards.Watcher("A"), Zone.Battlefield);
        game.Create(bob, TestCards.Watcher("B"), Zone.Battlefield);
        game.Move(game.State.Battlefield[0], Zone.Graveyard, MoveCause.Destroy);
        game.Move(game.State.Battlefield[0], Zone.Graveyard, MoveCause.Destroy);
        game.PassPriority(alice);

        var option = game.State.Choice!.Options[0].Id;
        Assert.Throws<InvalidOperationException>(() => game.Choose(bob, [option, option]));
    }

    [Fact]
    public void A_game_interrupted_by_a_choice_still_replays()
    {
        var (game, alice, bob) = InMainPhase();
        game.Create(bob, TestCards.Legend("Karn"), Zone.Battlefield);
        game.Create(bob, TestCards.Legend("Karn"), Zone.Battlefield);
        game.PassPriority(alice);
        game.Choose(bob, [game.State.Choice!.Options[0].Id]);
        TestCards.PassToTurn(game, 2);

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }
}
