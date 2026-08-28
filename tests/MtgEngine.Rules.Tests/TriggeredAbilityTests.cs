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

        game.BeginPlay(withMulligans: false);
        TestCards.PassToStep(game, TurnStep.PrecombatMain);
        return (game, alice, bob);
    }

    /// <summary>A trigger that must be pointed at a creature (CR 603.3d).</summary>
    private static TriggeredAbilityDefinition OnEnterTargetCreature() => new()
    {
        Id = "etb-target",
        Text = "When this enters, put a +1/+1 counter on target creature.",
        Triggers = (e, state, source) =>
            e is ObjectMoved { To: Zone.Battlefield } m && m.NewId == source.Id,
        Targets =
        [
            new TargetSpec
            {
                Kind = TargetKind.Permanent,
                Description = "target creature",
                ObjectFilter = (state, abilities, obj, controller) =>
                    Characteristics.Of(state, abilities, obj).IsCreature,
            },
        ],
        Effects = [new PutCounters(CounterKinds.PlusOnePlusOne, 1)],
    };

    /// <summary>A trigger that can only be pointed across the table (CR 109.5).</summary>
    private static TriggeredAbilityDefinition OnEnterTargetOpponentCreature() => new()
    {
        Id = "etb-target-theirs",
        Text = "When this enters, put a +1/+1 counter on target creature an opponent controls.",
        Triggers = (e, state, source) =>
            e is ObjectMoved { To: Zone.Battlefield } m && m.NewId == source.Id,
        Targets =
        [
            new TargetSpec
            {
                Kind = TargetKind.Permanent,
                Description = "target creature an opponent controls",
                ObjectFilter = (state, abilities, obj, controller) =>
                    obj.ControllerId != controller
                    && Characteristics.Of(state, abilities, obj).IsCreature,
            },
        ],
        Effects = [new PutCounters(CounterKinds.PlusOnePlusOne, 1)],
    };

    [Fact]
    public void A_targeting_trigger_asks_its_controller_for_a_target()
    {
        // CR 603.3d: the targets are chosen as the ability goes on the stack, which is why an
        // opponent can respond knowing what it is aimed at.
        var (game, alice, _) = InMainPhase(new Abilities(("watcher", OnEnterTargetCreature())));
        game.Create(alice, TestCards.Creature("Bear"), Zone.Battlefield);
        // Created in hand and moved, because an enters-the-battlefield trigger watches for the
        // move (CR 603.6a) and a permanent conjured straight onto the battlefield never made one.
        game.Move(game.Create(alice, TestCards.Watcher(), Zone.Hand), Zone.Battlefield, MoveCause.Resolve);

        TestCards.PassUntil(game, () => game.State.IsWaitingForChoice);

        Assert.Equal(ChoiceKind.ChooseTriggerTargets, game.State.Choice!.Kind);
        Assert.Equal(alice, game.State.Choice.PlayerId);
    }

    [Fact]
    public void Two_of_the_same_card_are_offered_as_distinguishable_options()
    {
        // A board with the same creature on both sides offers two buttons reading "Bear", and a
        // player cannot tell which one they are pointing at. Whose it is goes on the label.
        var (game, alice, bob) = InMainPhase(new Abilities(("watcher", OnEnterTargetCreature())));
        game.Create(alice, TestCards.Creature("Bear"), Zone.Battlefield);
        game.Create(bob, TestCards.Creature("Bear"), Zone.Battlefield);
        game.Move(game.Create(alice, TestCards.Watcher(), Zone.Hand), Zone.Battlefield, MoveCause.Resolve);

        TestCards.PassUntil(game, () => game.State.IsWaitingForChoice);

        var labels = game.State.Choice!.Options.Select(o => o.Label).ToList();
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(labels, l => l.Contains("(yours)", StringComparison.Ordinal));
        Assert.Contains(labels, l => l.Contains("Bob", StringComparison.Ordinal));
    }

    [Fact]
    public void A_targeting_trigger_carries_the_chosen_target_onto_the_stack()
    {
        var (game, alice, _) = InMainPhase(new Abilities(("watcher", OnEnterTargetCreature())));
        var bear = game.Create(alice, TestCards.Creature("Bear"), Zone.Battlefield);
        // Created in hand and moved, because an enters-the-battlefield trigger watches for the
        // move (CR 603.6a) and a permanent conjured straight onto the battlefield never made one.
        game.Move(game.Create(alice, TestCards.Watcher(), Zone.Hand), Zone.Battlefield, MoveCause.Resolve);

        TestCards.PassUntil(game, () => game.State.IsWaitingForChoice);
        var choice = game.State.Choice!;
        var pick = choice.Options.Single(o => o.Id.EndsWith(bear.Value.ToString("N"), StringComparison.Ordinal));
        game.Choose(alice, [pick.Id]);

        var onStack = game.State.Stack.Select(id => game.State.GetObject(id)).Single();
        Assert.Equal(Target.ToPermanent(bear), onStack.Targets.Single());
    }

    [Fact]
    public void A_targeting_trigger_with_no_legal_target_never_reaches_the_stack()
    {
        // CR 603.3d. It must target a creature an opponent controls, and the opponent has none —
        // the source itself is a creature but is not a legal target for this one. The ability is
        // removed rather than sitting on the stack waiting to resolve into nothing.
        var (game, alice, _) = InMainPhase(
            new Abilities(("watcher", OnEnterTargetOpponentCreature())));
        // Created in hand and moved, because an enters-the-battlefield trigger watches for the
        // move (CR 603.6a) and a permanent conjured straight onto the battlefield never made one.
        game.Move(game.Create(alice, TestCards.Watcher(), Zone.Hand), Zone.Battlefield, MoveCause.Resolve);

        TestCards.PassUntil(game, () => game.State.PendingTriggers.IsEmpty, guard: 200);

        Assert.Empty(game.State.Stack);
        Assert.Empty(game.State.PendingTriggers);
        Assert.Contains(game.Log, e => e is TriggerRemovedForNoTargets);
    }

    [Fact]
    public void A_game_that_answered_a_trigger_target_still_replays_to_the_same_state()
    {
        // The property the engine rests on. A choice made mid-settle has to fold back the same
        // way, which is why the answer is an event rather than a captured continuation.
        var (game, alice, _) = InMainPhase(new Abilities(("watcher", OnEnterTargetCreature())));
        var bear = game.Create(alice, TestCards.Creature("Bear"), Zone.Battlefield);
        // Created in hand and moved, because an enters-the-battlefield trigger watches for the
        // move (CR 603.6a) and a permanent conjured straight onto the battlefield never made one.
        game.Move(game.Create(alice, TestCards.Watcher(), Zone.Hand), Zone.Battlefield, MoveCause.Resolve);

        TestCards.PassUntil(game, () => game.State.IsWaitingForChoice);
        var choice = game.State.Choice!;
        game.Choose(
            alice,
            [choice.Options.Single(o => o.Id.EndsWith(bear.Value.ToString("N"), StringComparison.Ordinal)).Id]);

        Assert.Equal(game.State, Engine.GameReducer.Replay(game.Log));
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
        game.BeginPlay(withMulligans: false);
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
