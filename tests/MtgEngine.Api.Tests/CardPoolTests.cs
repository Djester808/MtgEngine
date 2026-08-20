using MtgEngine.Api.Cards;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The card pool: real cards, played by the engine.
/// </summary>
/// <remarks>
/// Each of these plays a card the way a game would — cast it, let it resolve, look at what
/// happened — rather than asserting the definition has the shape it was written with. A test
/// that checks a card's declaration is a test that the file says what the file says.
/// </remarks>
public sealed class CardPoolTests
{
    private static readonly CardPool Pool = new();

    private static CardDefinition Card(
        string name,
        CardType types = CardType.Instant,
        string? text = "does something",
        int? power = null,
        int? toughness = null,
        params string[] subtypes) => new()
        {
            OracleId = "oracle-" + name.ToLowerInvariant().Replace(' ', '-'),
            Name = name,
            CardTypes = types,
            OracleText = text ?? string.Empty,
            Power = power,
            Toughness = toughness,
            Subtypes = subtypes,
        };

    private static CardDefinition Vanilla(string name, int power, int toughness, params string[] subtypes) =>
        Card(name, CardType.Creature, text: null, power, toughness, subtypes);

    private static CardDefinition BasicLand(string name) => new()
    {
        OracleId = "oracle-" + name.ToLowerInvariant(),
        Name = name,
        CardTypes = CardType.Land,
        Supertypes = ["Basic"],
        Subtypes = [name],
    };

    private static (Game Game, Guid Alice, Guid Bob) InMainPhase()
    {
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var deck = Enumerable.Range(1, 40).Select(i => Vanilla($"Filler {i}", 1, 1)).ToList();

        var game = Game.Start(
            Guid.NewGuid(),
            [
                new PlayerSetup(alice, "Alice", 20, deck),
                new PlayerSetup(bob, "Bob", 20, deck),
            ],
            new GameRandom(7),
            startingPlayerId: alice,
            abilities: Pool);

        game.BeginPlay(withMulligans: false);
        PassToMain(game);
        return (game, alice, bob);
    }

    private static void PassToMain(Game game)
    {
        for (var guard = 0; guard < 200 && game.State.CurrentStep != TurnStep.PrecombatMain; guard++)
        {
            if (AnswerAnything(game))
                continue;

            if (game.State.CurrentStep == TurnStep.DeclareAttackers && !game.State.Combat.AttackersDeclared)
            {
                game.DeclareAttackers(game.State.ActivePlayerId, new Dictionary<ObjectId, Guid>());
                continue;
            }

            game.PassPriority(game.State.Priority.Holder!.Value);
        }
    }

    /// <summary>
    /// Plays on until the stack is empty and nothing is waiting to go on it.
    /// </summary>
    /// <remarks>
    /// A trigger that has fired is not on the stack yet (CR 117.2a) — it gets there the next time
    /// a player would receive priority — so stopping at "the stack is empty" stops one step too
    /// early and misses everything a trigger was going to do.
    /// </remarks>
    /// <summary>
    /// Answers whatever the game is waiting on, the way that changes nothing.
    /// </summary>
    /// <remarks>
    /// Discarding to hand size is a decision like any other (CR 514.1), so a test walking
    /// several turns has to answer it. Taking the first options keeps the test about the card it
    /// is testing rather than about which card to pitch.
    /// </remarks>
    private static bool AnswerAnything(Game game)
    {
        if (game.State.Choice is not { } choice)
            return false;

        game.Choose(choice.PlayerId, [.. choice.Options.Take(choice.MinPicks).Select(o => o.Id)]);
        return true;
    }

    private static void ResolveTop(Game game)
    {
        for (var guard = 0; guard < 100; guard++)
        {
            if (game.State.Stack.IsEmpty && game.State.PendingTriggers.IsEmpty)
                return;

            if (game.State.Priority.Holder is not { } holder)
                return;

            game.PassPriority(holder);
        }
    }

    [Fact]
    public void A_forest_taps_for_green()
    {
        var (game, alice, _) = InMainPhase();
        var forest = game.PlayLand(alice, game.Create(alice, BasicLand("Forest"), Zone.Hand));

        game.ActivateAbility(alice, forest, "mana");

        Assert.Equal(1, game.State.GetPlayer(alice).ManaPool[ManaColor.Green]);
    }

    [Fact]
    public void Every_basic_land_taps_for_its_colour()
    {
        var (game, alice, _) = InMainPhase();

        foreach (var (name, color) in new[]
        {
            ("Plains", ManaColor.White), ("Island", ManaColor.Blue), ("Swamp", ManaColor.Black),
            ("Mountain", ManaColor.Red), ("Forest", ManaColor.Green),
        })
        {
            var land = game.Create(alice, BasicLand(name), Zone.Battlefield);
            game.ActivateAbility(alice, land, "mana");
            Assert.Equal(1, game.State.GetPlayer(alice).ManaPool[color]);
            game.Move(land, Zone.Graveyard, MoveCause.Destroy);
        }
    }

    [Fact]
    public void Lightning_bolt_burns_a_player()
    {
        var (game, alice, bob) = InMainPhase();
        var bolt = game.Create(alice, Card("Lightning Bolt"), Zone.Hand);

        game.CastSpell(alice, bolt, [Target.ToPlayer(bob)]);
        ResolveTop(game);

        Assert.Equal(17, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void Lightning_bolt_also_burns_a_creature()
    {
        // "Any target" means creature or player, which is why it is its own target kind.
        var (game, alice, bob) = InMainPhase();
        var bear = game.Create(bob, Vanilla("Bear", 2, 2), Zone.Battlefield);
        var bolt = game.Create(alice, Card("Lightning Bolt"), Zone.Hand);

        game.CastSpell(alice, bolt, [Target.ToPermanent(bear)]);
        ResolveTop(game);

        Assert.Empty(game.State.Battlefield);
    }

    [Fact]
    public void Murder_destroys_a_creature()
    {
        var (game, alice, bob) = InMainPhase();
        var bear = game.Create(bob, Vanilla("Bear", 5, 5), Zone.Battlefield);
        var murder = game.Create(alice, Card("Murder"), Zone.Hand);

        game.CastSpell(alice, murder, [Target.ToPermanent(bear)]);
        ResolveTop(game);

        Assert.Empty(game.State.Battlefield);
        Assert.Single(game.State.GetPlayer(bob).Graveyard);
    }

    [Fact]
    public void Counterspell_stops_a_spell()
    {
        var (game, alice, bob) = InMainPhase();
        var bolt = game.Create(alice, Card("Lightning Bolt"), Zone.Hand);
        var counter = game.Create(bob, Card("Counterspell"), Zone.Hand);

        var onStack = game.CastSpell(alice, bolt, [Target.ToPlayer(bob)]);
        game.PassPriority(alice);
        game.CastSpell(bob, counter, [Target.ToSpell(onStack)]);
        ResolveTop(game);

        Assert.Equal(20, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void Divination_draws_two()
    {
        var (game, alice, _) = InMainPhase();
        var divination = game.Create(alice, Card("Divination"), Zone.Hand);
        var before = game.State.GetPlayer(alice).Hand.Count;

        game.CastSpell(alice, divination);
        ResolveTop(game);

        Assert.Equal(before + 1, game.State.GetPlayer(alice).Hand.Count);
    }

    [Fact]
    public void Giant_growth_wears_off_at_end_of_turn()
    {
        // CR 514.2: the effect ends during cleanup, not when the spell leaves the stack.
        var (game, alice, _) = InMainPhase();
        var bear = game.Create(alice, Vanilla("Bear", 2, 2), Zone.Battlefield);
        var growth = game.Create(alice, Card("Giant Growth"), Zone.Hand);

        game.CastSpell(alice, growth, [Target.ToPermanent(bear)]);
        ResolveTop(game);
        Assert.Equal(5, game.CharacteristicsOf(bear).Power);

        PassToNextTurn(game);
        Assert.Equal(2, game.CharacteristicsOf(bear).Power);
    }

    [Fact]
    public void Sol_ring_adds_two_colourless()
    {
        var (game, alice, _) = InMainPhase();
        var ring = game.Create(alice, Card("Sol Ring", CardType.Artifact), Zone.Battlefield);

        game.ActivateAbility(alice, ring, "mana");

        Assert.Equal(2, game.State.GetPlayer(alice).ManaPool.Colorless);
    }

    [Fact]
    public void Elvish_archdruid_pumps_other_elves_and_not_itself()
    {
        var (game, alice, _) = InMainPhase();
        var druid = game.Create(alice, Vanilla("Elvish Archdruid", 2, 2, "Elf"), Zone.Battlefield);
        var elf = game.Create(alice, Vanilla("Llanowar Elves", 1, 1, "Elf"), Zone.Battlefield);
        var bear = game.Create(alice, Vanilla("Bear", 2, 2, "Bear"), Zone.Battlefield);

        Assert.Equal(2, game.CharacteristicsOf(elf).Power);
        Assert.Equal(2, game.CharacteristicsOf(druid).Power);
        Assert.Equal(2, game.CharacteristicsOf(bear).Power);

        game.Move(druid, Zone.Graveyard, MoveCause.Destroy);

        Assert.Equal(1, game.CharacteristicsOf(elf).Power);
    }

    [Fact]
    public void Kalonian_hydra_is_never_on_the_battlefield_without_its_counters()
    {
        // CR 614.1c: the counters arrive as part of the move, not afterwards.
        var (game, alice, _) = InMainPhase();
        var hydra = game.Create(alice, Vanilla("Kalonian Hydra", 0, 0, "Hydra"), Zone.Hand);

        game.CastSpell(alice, hydra);
        ResolveTop(game);

        var permanent = game.State.GetObject(game.State.Battlefield.Single());
        Assert.Equal(4, permanent.Permanent!.Counters[CounterKinds.PlusOnePlusOne]);
        // A 0/0 that arrived without them would have died to state-based actions immediately.
        Assert.Equal(4, game.CharacteristicsOf(permanent.Id).Toughness);
    }

    [Fact]
    public void Wall_of_blossoms_draws_when_it_enters()
    {
        var (game, alice, _) = InMainPhase();
        var wall = game.Create(alice, Vanilla("Wall of Blossoms", 0, 4, "Wall"), Zone.Hand);
        var before = game.State.GetPlayer(alice).Hand.Count;

        game.CastSpell(alice, wall);
        ResolveTop(game);

        Assert.Equal(before, game.State.GetPlayer(alice).Hand.Count);
        Assert.Single(game.State.Battlefield);
    }

    [Fact]
    public void Solemn_simulacrum_draws_when_it_dies()
    {
        var (game, alice, _) = InMainPhase();
        var solemn = game.Create(alice, Vanilla("Solemn Simulacrum", 2, 2), Zone.Battlefield);
        var before = game.State.GetPlayer(alice).Hand.Count;

        game.Move(solemn, Zone.Graveyard, MoveCause.Destroy);
        ResolveTop(game);

        Assert.Equal(before + 1, game.State.GetPlayer(alice).Hand.Count);
    }

    [Fact]
    public void Prodigal_pyromancer_pings_once_it_can_tap()
    {
        var (game, alice, bob) = InMainPhase();
        var pyromancer = game.Create(alice, Vanilla("Prodigal Pyromancer", 1, 1), Zone.Battlefield);

        // CR 302.6: not this turn.
        Assert.Throws<InvalidOperationException>(() =>
            game.ActivateAbility(alice, pyromancer, "ping", [Target.ToPlayer(bob)]));

        PassToNextTurn(game);
        PassToNextTurn(game);
        PassToMain(game);

        game.ActivateAbility(alice, pyromancer, "ping", [Target.ToPlayer(bob)]);
        ResolveTop(game);

        Assert.Equal(19, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void Resolving_ties_a_card_to_its_oracle_id()
    {
        // The pool is written against names because a name can be checked by eye and a GUID
        // cannot. Once resolved it matches on oracle id, which is what a deck stores and what
        // survives a card being renamed.
        var pool = new CardPool();
        // CardDefinition is a class, not a record, so the renamed copy is built rather than
        // `with`-ed.
        var renamed = new CardDefinition
        {
            OracleId = "real-bolt-id",
            Name = "Renamed",
            CardTypes = CardType.Instant,
            OracleText = "deals 3 damage to any target",
        };

        Assert.Null(pool.SpellOf(renamed));

        pool.ResolveOracleIds(new Dictionary<string, string> { ["Lightning Bolt"] = "real-bolt-id" });

        Assert.NotNull(pool.SpellOf(renamed));
        Assert.Equal(1, pool.ResolvedCount);
    }

    [Fact]
    public void An_unresolved_pool_still_plays_by_name()
    {
        // A card database that is missing or still downloading costs accuracy across renames,
        // not the ability to start a game.
        var pool = new CardPool();

        Assert.NotNull(pool.SpellOf(Card("Lightning Bolt")));
        Assert.Equal(0, pool.ResolvedCount);
    }

    [Fact]
    public void A_card_the_pool_does_not_know_is_not_known()
    {
        Assert.True(Pool.Knows("Lightning Bolt"));
        Assert.True(Pool.Knows("Forest"));
        Assert.False(Pool.Knows("Black Lotus"));
        Assert.True(Pool.Count > 10);
    }

    [Fact]
    public void A_real_game_of_these_cards_still_replays()
    {
        var (game, alice, bob) = InMainPhase();
        var forest = game.PlayLand(alice, game.Create(alice, BasicLand("Forest"), Zone.Hand));
        game.ActivateAbility(alice, forest, "mana");
        game.CastSpell(alice, game.Create(alice, Card("Lightning Bolt"), Zone.Hand), [Target.ToPlayer(bob)]);
        ResolveTop(game);
        PassToNextTurn(game);

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }

    private static void PassToNextTurn(Game game)
    {
        var turn = game.State.TurnNumber;
        for (var guard = 0; guard < 400 && game.State.TurnNumber == turn; guard++)
        {
            if (AnswerAnything(game))
                continue;

            if (game.State.TurnNumber != turn)
                return;

            if (game.State.CurrentStep == TurnStep.DeclareAttackers && !game.State.Combat.AttackersDeclared)
            {
                game.DeclareAttackers(game.State.ActivePlayerId, new Dictionary<ObjectId, Guid>());
                continue;
            }

            game.PassPriority(game.State.Priority.Holder!.Value);
        }
    }
}
