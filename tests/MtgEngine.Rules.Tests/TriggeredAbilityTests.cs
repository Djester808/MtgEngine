using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Triggered abilities (CR 603).
/// </summary>
/// <remarks>
/// The previous engine declared an <c>ITriggeredAbility</c> interface and a list of
/// <c>GameEvent</c> records, and then never collected a trigger anywhere — nothing put one on
/// the stack, so no card could ever have done anything when something happened.
/// <para>
/// The three rules that shape everything here: an ability triggers the moment its event happens
/// but nothing happens then (CR 117.2a, 603.2); it goes on the stack the next time a player
/// would receive priority (CR 603.3); and simultaneous triggers go on in APNAP order
/// (CR 603.3b), so the last player's resolve first.
/// </para>
/// </remarks>
public sealed class TriggeredAbilityTests
{
    /// <summary>An ability source built from a lambda, so a test can state its own trigger.</summary>
    private sealed class Abilities(params (string OracleFragment, TriggeredAbilityDefinition Ability)[] defs)
        : IAbilitySource
    {
        public IReadOnlyList<TriggeredAbilityDefinition> TriggersOf(CardDefinition card) =>
            [.. defs.Where(d => card.OracleId.Contains(d.OracleFragment, StringComparison.Ordinal))
                .Select(d => d.Ability)];
    }

    /// <summary>"Whenever a creature dies, ..." — the standard shape (CR 603.2).</summary>
    private static TriggeredAbilityDefinition OnCreatureDies(string id = "dies") => new()
    {
        Id = id,
        Text = "Whenever a creature dies, its controller draws a card.",
        Triggers = (e, state, source) =>
            e is ObjectMoved { To: Zone.Graveyard, From: Zone.Battlefield },
    };

    private static (Game Game, Guid Alice, Guid Bob) InMainPhase(IAbilitySource abilities)
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
            abilities: abilities);

        game.BeginPlay();
        TestCards.PassToStep(game, TurnStep.PrecombatMain);
        return (game, alice, bob);
    }

    [Fact]
    public void Nothing_happens_at_the_moment_an_ability_triggers()
    {
        // CR 117.2a: "nothing actually happens at the time an ability triggers".
        var (game, alice, _) = InMainPhase(new Abilities(("watcher", OnCreatureDies())));
        game.Create(alice, TestCards.Watcher(), Zone.Battlefield);
        var victim = game.Create(alice, TestCards.Creature("Doomed"), Zone.Battlefield);

        game.Move(victim, Zone.Graveyard, MoveCause.Destroy);

        Assert.Single(game.State.PendingTriggers);
        Assert.Empty(game.State.Stack);
    }

    [Fact]
    public void A_waiting_trigger_goes_on_the_stack_when_a_player_would_get_priority()
    {
        // CR 603.3.
        var (game, alice, _) = InMainPhase(new Abilities(("watcher", OnCreatureDies())));
        game.Create(alice, TestCards.Watcher(), Zone.Battlefield);
        var victim = game.Create(alice, TestCards.Creature("Doomed"), Zone.Battlefield);
        game.Move(victim, Zone.Graveyard, MoveCause.Destroy);

        game.PassPriority(alice);

        Assert.Empty(game.State.PendingTriggers);
        Assert.Single(game.State.Stack);
        Assert.NotNull(game.State.GetObject(game.State.Stack[0]).Ability);
    }

    [Fact]
    public void An_ability_on_the_stack_is_not_a_card()
    {
        // CR 113.7a and 405.4: it has the text of the ability and no other characteristics, and
        // it was never a card, so it has no graveyard to go to.
        var (game, alice, bob) = InMainPhase(new Abilities(("watcher", OnCreatureDies())));
        game.Create(alice, TestCards.Watcher(), Zone.Battlefield);
        var victim = game.Create(alice, TestCards.Creature("Doomed"), Zone.Battlefield);
        game.Move(victim, Zone.Graveyard, MoveCause.Destroy);
        game.PassPriority(alice);

        var graveyardBefore = game.State.GetPlayer(alice).Graveyard.Count;
        game.PassPriority(game.State.Priority.Holder!.Value);
        game.PassPriority(game.State.Priority.Holder!.Value);

        Assert.Empty(game.State.Stack);
        Assert.Equal(graveyardBefore, game.State.GetPlayer(alice).Graveyard.Count);
        Assert.Contains(game.Log, e => e is ObjectCeasedToExist);
    }

    [Fact]
    public void A_trigger_still_happens_when_its_source_has_gone()
    {
        // CR 603.6: the ability triggered, and that is enough. A creature that dies to the same
        // event that triggered it still gets its trigger.
        var dies = new TriggeredAbilityDefinition
        {
            Id = "self",
            Text = "When this creature dies, draw a card.",
            Triggers = (e, state, source) =>
                e is ObjectMoved { To: Zone.Graveyard, From: Zone.Battlefield } m
                && m.OldId == source.Id,
        };
        var (game, alice, _) = InMainPhase(new Abilities(("watcher", dies)));
        var watcher = game.Create(alice, TestCards.Watcher(), Zone.Battlefield);

        game.Move(watcher, Zone.Graveyard, MoveCause.Destroy);
        game.PassPriority(alice);

        Assert.Single(game.State.Stack);
        Assert.Equal("When this creature dies, draw a card.",
            game.State.GetObject(game.State.Stack[0]).Ability!.Text);
    }

    [Fact]
    public void Simultaneous_triggers_go_on_the_stack_in_apnap_order()
    {
        // CR 603.3b: the active player's go on lowest, so they resolve last. With Alice active,
        // Bob's trigger ends up on top and resolves first.
        var (game, alice, bob) = InMainPhase(new Abilities(("watcher", OnCreatureDies())));
        game.Create(alice, TestCards.Watcher("Alice Watcher"), Zone.Battlefield);
        game.Create(bob, TestCards.Watcher("Bob Watcher"), Zone.Battlefield);
        var victim = game.Create(alice, TestCards.Creature("Doomed"), Zone.Battlefield);

        game.Move(victim, Zone.Graveyard, MoveCause.Destroy);
        game.PassPriority(alice);

        Assert.Equal(2, game.State.Stack.Count);
        Assert.Equal(bob, game.State.GetObject(game.State.Stack[0]).ControllerId);
        Assert.Equal(alice, game.State.GetObject(game.State.Stack[1]).ControllerId);
    }

    [Fact]
    public void Apnap_order_follows_the_active_player_around_a_four_player_table()
    {
        // The same rule with four seats: whoever is active goes lowest, then turn order.
        var abilities = new Abilities(("watcher", OnCreatureDies()));
        var seats = Enumerable.Range(1, 4)
            .Select(i => new Guid($"{i:D8}-0000-0000-0000-000000000000"))
            .ToList();
        var game = Game.Start(
            Guid.NewGuid(),
            [.. seats.Select((id, i) => new PlayerSetup(id, $"P{i + 1}", 40, TestCards.Deck(40, $"P{i + 1}")))],
            new GameRandom(2),
            startingPlayerId: seats[0],
            abilities: abilities);
        game.BeginPlay();
        TestCards.PassToStep(game, TurnStep.PrecombatMain);

        foreach (var seat in seats)
            game.Create(seat, TestCards.Watcher($"W{seat}"), Zone.Battlefield);
        var victim = game.Create(seats[0], TestCards.Creature("Doomed"), Zone.Battlefield);

        game.Move(victim, Zone.Graveyard, MoveCause.Destroy);
        game.PassPriority(seats[0]);

        // Lowest on the stack is the active player's, so the top is the last in turn order.
        var controllers = game.State.Stack
            .Select(id => game.State.GetObject(id).ControllerId)
            .ToList();
        Assert.Equal([seats[3], seats[2], seats[1], seats[0]], controllers);
    }

    [Fact]
    public void A_trigger_only_fires_from_the_zone_it_functions_in()
    {
        // CR 603.6: an ability that functions on the battlefield does nothing from a hand.
        var (game, alice, _) = InMainPhase(new Abilities(("watcher", OnCreatureDies())));
        TestCards.PutInHand(game, alice, TestCards.Watcher());
        var victim = game.Create(alice, TestCards.Creature("Doomed"), Zone.Battlefield);

        game.Move(victim, Zone.Graveyard, MoveCause.Destroy);

        Assert.Empty(game.State.PendingTriggers);
    }

    [Fact]
    public void An_ability_triggers_once_per_event()
    {
        // CR 603.2c.
        var (game, alice, _) = InMainPhase(new Abilities(("watcher", OnCreatureDies())));
        game.Create(alice, TestCards.Watcher(), Zone.Battlefield);
        var first = game.Create(alice, TestCards.Creature("First"), Zone.Battlefield);
        var second = game.Create(alice, TestCards.Creature("Second"), Zone.Battlefield);

        game.Move(first, Zone.Graveyard, MoveCause.Destroy);
        game.Move(second, Zone.Graveyard, MoveCause.Destroy);

        Assert.Equal(2, game.State.PendingTriggers.Count);
    }

    [Fact]
    public void A_trigger_from_a_state_based_action_still_reaches_the_stack()
    {
        // CR 704.3's loop: an SBA kills the creature, that death triggers something, the trigger
        // goes on the stack, and the check runs again. This is the interleaving the previous
        // engine could not produce, because it ran SBAs after every mutation and collected no
        // triggers at all.
        var (game, alice, _) = InMainPhase(new Abilities(("watcher", OnCreatureDies())));
        game.Create(alice, TestCards.Watcher(), Zone.Battlefield);
        var doomed = game.Create(alice, TestCards.Creature("Doomed", 1, 1), Zone.Battlefield);

        game.MarkDamage(doomed, 1);
        game.PassPriority(alice);

        Assert.Single(game.State.Stack);
        Assert.NotNull(game.State.GetObject(game.State.Stack[0]).Ability);
    }

    [Fact]
    public void A_game_with_triggers_still_replays_to_the_same_state()
    {
        var (game, alice, bob) = InMainPhase(new Abilities(("watcher", OnCreatureDies())));
        game.Create(alice, TestCards.Watcher(), Zone.Battlefield);
        var victim = game.Create(alice, TestCards.Creature("Doomed"), Zone.Battlefield);
        game.Move(victim, Zone.Graveyard, MoveCause.Destroy);
        game.PassPriority(alice);
        game.PassPriority(bob);
        game.PassPriority(alice);

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }
}
