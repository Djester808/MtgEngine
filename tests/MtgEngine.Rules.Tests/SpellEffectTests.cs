using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Casting with costs and targets, and what a spell does when it resolves (CR 601, 608).
/// </summary>
/// <remarks>
/// This is the first slice where a card actually does something. The previous engine never got
/// here: <c>IStackObject</c> carried a <c>Description</c> string and nothing else, so resolving a
/// spell could not affect the game, and every other subsystem was built around that hole.
/// </remarks>
public sealed class SpellEffectTests
{
    /// <summary>A card pool built from a dictionary, standing in for slice 8's real one.</summary>
    private sealed class Pool : IAbilitySource
    {
        private readonly Dictionary<string, SpellDefinition> _spells = [];
        private readonly Dictionary<string, List<ActivatedAbilityDefinition>> _activated = [];

        public IReadOnlyList<TriggeredAbilityDefinition> TriggersOf(CardDefinition card) => [];

        public SpellDefinition? SpellOf(CardDefinition card) =>
            _spells.GetValueOrDefault(card.OracleId);

        public IReadOnlyList<ActivatedAbilityDefinition> ActivatedOf(CardDefinition card) =>
            _activated.TryGetValue(card.OracleId, out var list) ? list : [];

        public Pool WithSpell(CardDefinition card, SpellDefinition definition)
        {
            _spells[card.OracleId] = definition;
            return this;
        }

        public Pool WithAbility(CardDefinition card, ActivatedAbilityDefinition ability)
        {
            if (!_activated.TryGetValue(card.OracleId, out var list))
                _activated[card.OracleId] = list = [];

            list.Add(ability);
            return this;
        }
    }

    private static readonly TargetSpec AnyPlayer = new()
    {
        Kind = TargetKind.Player,
        Description = "any player",
    };

    private static readonly TargetSpec AnyCreature = new()
    {
        Kind = TargetKind.Permanent,
        Description = "target creature",
        ObjectFilter = (state, abilities, obj, controller) =>
            Characteristics.Of(state, abilities, obj).IsCreature,
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

    private static void Resolve(Game game, Guid alice, Guid bob)
    {
        game.PassPriority(game.State.Priority.Holder!.Value);
        if (game.State.Priority.Holder is { } next && !game.State.Stack.IsEmpty)
            game.PassPriority(next);
    }

    [Fact]
    public void A_burn_spell_damages_the_player_it_targeted()
    {
        var bolt = TestCards.Instant("Bolt");
        var pool = new Pool().WithSpell(bolt, new SpellDefinition
        {
            Targets = [AnyPlayer],
            Effects = [new DealDamage(3)],
        });
        var (game, alice, bob) = InMainPhase(pool);
        var card = TestCards.PutInHand(game, alice, bolt);

        game.CastSpell(alice, card, [Target.ToPlayer(bob)]);
        Resolve(game, alice, bob);

        Assert.Equal(17, game.State.GetPlayer(bob).Life);
        Assert.Single(game.State.GetPlayer(alice).Graveyard);
    }

    [Fact]
    public void A_burn_spell_can_kill_a_creature()
    {
        var bolt = TestCards.Instant("Bolt");
        var pool = new Pool().WithSpell(bolt, new SpellDefinition
        {
            Targets = [AnyCreature],
            Effects = [new DealDamage(3)],
        });
        var (game, alice, bob) = InMainPhase(pool);
        var bear = game.Create(bob, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        var card = TestCards.PutInHand(game, alice, bolt);

        game.CastSpell(alice, card, [Target.ToPermanent(bear)]);
        Resolve(game, alice, bob);

        Assert.Empty(game.State.Battlefield);
    }

    [Fact]
    public void A_spell_cannot_be_cast_at_an_illegal_target()
    {
        // CR 601.2c: targets are chosen as the spell is cast, and must be legal then.
        var bolt = TestCards.Instant("Bolt");
        var pool = new Pool().WithSpell(bolt, new SpellDefinition
        {
            Targets = [AnyCreature],
            Effects = [new DealDamage(3)],
        });
        var (game, alice, _) = InMainPhase(pool);
        var enchantment = game.Create(alice, TestCards.Anthem(), Zone.Battlefield);
        var card = TestCards.PutInHand(game, alice, bolt);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            game.CastSpell(alice, card, [Target.ToPermanent(enchantment)]));

        Assert.Contains("target creature", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spell_whose_only_target_has_gone_does_nothing_at_all()
    {
        // CR 608.2b. Not just the targeted part — nothing the spell would have done happens.
        var bolt = TestCards.Instant("Bolt");
        var pool = new Pool().WithSpell(bolt, new SpellDefinition
        {
            Targets = [AnyCreature],
            Effects = [new DealDamage(3), new DrawCards(1)],
        });
        var (game, alice, bob) = InMainPhase(pool);
        var bear = game.Create(bob, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        var card = TestCards.PutInHand(game, alice, bolt);
        var handBefore = game.State.GetPlayer(alice).Hand.Count;

        game.CastSpell(alice, card, [Target.ToPermanent(bear)]);
        game.Move(bear, Zone.Graveyard, MoveCause.Destroy);
        Resolve(game, alice, bob);

        Assert.Contains(game.Log, e => e is FizzledForIllegalTargets);
        // The draw was part of the same spell and does not happen either.
        Assert.Equal(handBefore - 1, game.State.GetPlayer(alice).Hand.Count);
    }

    [Fact]
    public void A_counterspell_stops_a_spell_resolving()
    {
        // CR 701.5a.
        var creature = TestCards.Creature("Ox", 3, 3);
        var counter = TestCards.Instant("Counterspell");
        var pool = new Pool().WithSpell(counter, new SpellDefinition
        {
            Targets = [new TargetSpec { Kind = TargetKind.SpellOnStack, Description = "target spell" }],
            Effects = [new CounterTargetSpell()],
        });
        var (game, alice, bob) = InMainPhase(pool);
        var creatureCard = TestCards.PutInHand(game, alice, creature);
        var counterCard = TestCards.PutInHand(game, bob, counter);

        var onStack = game.CastSpell(alice, creatureCard);
        game.PassPriority(alice);
        game.CastSpell(bob, counterCard, [Target.ToSpell(onStack)]);

        TestCards.PassUntil(game, () => game.State.Stack.IsEmpty);

        Assert.Empty(game.State.Battlefield);
        Assert.Contains(game.State.GetPlayer(alice).Graveyard,
            id => game.State.GetObject(id).Card.Name == "Ox");
    }

    [Fact]
    public void A_spell_draws_cards_for_its_controller()
    {
        var divination = TestCards.Instant("Divination");
        var pool = new Pool().WithSpell(divination, new SpellDefinition
        {
            Effects = [new DrawCards(2)],
        });
        var (game, alice, bob) = InMainPhase(pool);
        var card = TestCards.PutInHand(game, alice, divination);
        var before = game.State.GetPlayer(alice).Hand.Count;

        game.CastSpell(alice, card);
        Resolve(game, alice, bob);

        // One left hand to be cast, two came back.
        Assert.Equal(before + 1, game.State.GetPlayer(alice).Hand.Count);
    }

    [Fact]
    public void A_spell_can_put_counters_on_a_creature()
    {
        var growth = TestCards.Instant("Growth");
        var pool = new Pool().WithSpell(growth, new SpellDefinition
        {
            Targets = [AnyCreature],
            Effects = [new PutCounters(CounterKinds.PlusOnePlusOne, 2)],
        });
        var (game, alice, bob) = InMainPhase(pool);
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        var card = TestCards.PutInHand(game, alice, growth);

        game.CastSpell(alice, card, [Target.ToPermanent(bear)]);
        Resolve(game, alice, bob);

        Assert.Equal(4, game.CharacteristicsOf(bear).Power);
    }

    [Fact]
    public void A_spell_costs_mana_and_cannot_be_cast_without_it()
    {
        // CR 601.2h.
        var costed = TestCards.Costed("Soldier", "{1}{W}", 2);
        var (game, alice, _) = InMainPhase(new Pool());
        var card = TestCards.PutInHand(game, alice, costed);

        var ex = Assert.Throws<InvalidOperationException>(() => game.CastSpell(alice, card));

        Assert.Contains("601.2h", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Zone.Hand, game.State.GetObject(card).Zone);
    }

    [Fact]
    public void A_mana_ability_adds_mana_without_using_the_stack()
    {
        // CR 605.3b: it resolves immediately and nobody can respond.
        var forest = TestCards.BasicLand("Forest");
        var pool = new Pool().WithAbility(forest, new ActivatedAbilityDefinition
        {
            Id = "tap-for-green",
            Text = "{T}: Add {G}.",
            RequiresTap = true,
            Produces = [new ManaProduction(ManaColor.Green)],
        });
        var (game, alice, _) = InMainPhase(pool);
        var land = game.Create(alice, forest, Zone.Battlefield);

        var stackId = game.ActivateAbility(alice, land, "tap-for-green");

        Assert.Null(stackId);
        Assert.Empty(game.State.Stack);
        Assert.Equal(1, game.State.GetPlayer(alice).ManaPool[ManaColor.Green]);
        Assert.True(game.State.GetObject(land).Permanent!.IsTapped);
    }

    [Fact]
    public void Mana_from_a_land_pays_for_a_spell()
    {
        var forest = TestCards.BasicLand("Forest");
        var costed = TestCards.Costed("Elf", "{G}", 1);
        var pool = new Pool().WithAbility(forest, new ActivatedAbilityDefinition
        {
            Id = "tap-for-green",
            Text = "{T}: Add {G}.",
            RequiresTap = true,
            Produces = [new ManaProduction(ManaColor.Green)],
        });
        var (game, alice, _) = InMainPhase(pool);
        var land = game.Create(alice, forest, Zone.Battlefield);
        var card = TestCards.PutInHand(game, alice, costed);

        game.ActivateAbility(alice, land, "tap-for-green");
        game.CastSpell(alice, card);

        Assert.Single(game.State.Stack);
        Assert.True(game.State.GetPlayer(alice).ManaPool.IsEmpty);
    }

    [Fact]
    public void Unspent_mana_empties_when_the_step_ends()
    {
        // CR 500.5.
        var forest = TestCards.BasicLand("Forest");
        var pool = new Pool().WithAbility(forest, new ActivatedAbilityDefinition
        {
            Id = "tap-for-green",
            Text = "{T}: Add {G}.",
            RequiresTap = true,
            Produces = [new ManaProduction(ManaColor.Green)],
        });
        var (game, alice, _) = InMainPhase(pool);
        var land = game.Create(alice, forest, Zone.Battlefield);

        game.ActivateAbility(alice, land, "tap-for-green");
        Assert.False(game.State.GetPlayer(alice).ManaPool.IsEmpty);

        TestCards.PassToStep(game, TurnStep.BeginningOfCombat);

        Assert.True(game.State.GetPlayer(alice).ManaPool.IsEmpty);
        Assert.Contains(game.Log, e => e is ManaPoolsEmptied);
    }

    [Fact]
    public void A_non_mana_ability_uses_the_stack_and_can_be_responded_to()
    {
        // CR 602.2 and 605.1a: an ability with a target is never a mana ability.
        var pinger = TestCards.Creature("Pinger", 1, 1);
        var pool = new Pool().WithAbility(pinger, new ActivatedAbilityDefinition
        {
            Id = "ping",
            Text = "{T}: This creature deals 1 damage to any player.",
            RequiresTap = true,
            Targets = [AnyPlayer],
            Effects = [new DealDamage(1)],
        });
        var (game, alice, bob) = InMainPhase(pool);
        var creature = game.Create(alice, pinger, Zone.Battlefield);
        TestCards.PassToTurn(game, 3);
        TestCards.PassToStep(game, TurnStep.PrecombatMain);

        var stackId = game.ActivateAbility(alice, creature, "ping", [Target.ToPlayer(bob)]);

        Assert.NotNull(stackId);
        Assert.Single(game.State.Stack);

        TestCards.PassUntil(game, () => game.State.Stack.IsEmpty);
        Assert.Equal(19, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void A_summoning_sick_creature_cannot_use_a_tap_ability()
    {
        // CR 302.6.
        var pinger = TestCards.Creature("Pinger", 1, 1);
        var pool = new Pool().WithAbility(pinger, new ActivatedAbilityDefinition
        {
            Id = "ping",
            Text = "{T}: This creature deals 1 damage to any player.",
            RequiresTap = true,
            Targets = [AnyPlayer],
            Effects = [new DealDamage(1)],
        });
        var (game, alice, bob) = InMainPhase(pool);
        var creature = game.Create(alice, pinger, Zone.Battlefield);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            game.ActivateAbility(alice, creature, "ping", [Target.ToPlayer(bob)]));

        Assert.Contains("302.6", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_land_can_tap_for_mana_the_turn_it_arrives()
    {
        // CR 302.6 is about creatures. A land is not one, so it taps immediately.
        var forest = TestCards.BasicLand("Forest");
        var pool = new Pool().WithAbility(forest, new ActivatedAbilityDefinition
        {
            Id = "tap-for-green",
            Text = "{T}: Add {G}.",
            RequiresTap = true,
            Produces = [new ManaProduction(ManaColor.Green)],
        });
        var (game, alice, _) = InMainPhase(pool);
        var land = game.PlayLand(alice, TestCards.PutInHand(game, alice, forest));

        game.ActivateAbility(alice, land, "tap-for-green");

        Assert.Equal(1, game.State.GetPlayer(alice).ManaPool[ManaColor.Green]);
    }

    [Fact]
    public void A_game_with_spells_and_abilities_still_replays()
    {
        var bolt = TestCards.Instant("Bolt");
        var forest = TestCards.BasicLand("Forest");
        var pool = new Pool()
            .WithSpell(bolt, new SpellDefinition { Targets = [AnyPlayer], Effects = [new DealDamage(3)] })
            .WithAbility(forest, new ActivatedAbilityDefinition
            {
                Id = "tap-for-green",
                Text = "{T}: Add {G}.",
                RequiresTap = true,
                Produces = [new ManaProduction(ManaColor.Green)],
            });
        var (game, alice, bob) = InMainPhase(pool);
        var land = game.Create(alice, forest, Zone.Battlefield);
        game.ActivateAbility(alice, land, "tap-for-green");
        game.CastSpell(alice, TestCards.PutInHand(game, alice, bolt), [Target.ToPlayer(bob)]);
        TestCards.PassToTurn(game, 2);

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }
}
