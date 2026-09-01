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
                game.DeclareAttackers(game.State.ActivePlayerId, new Dictionary<ObjectId, AttackTarget>());
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

    /// <summary>A creature that cannot be destroyed (CR 702.12).</summary>
    private static CardDefinition Unkillable(string name = "Darksteel Myr") => new()
    {
        OracleId = "oracle-" + name.ToLowerInvariant().Replace(' ', '-'),
        Name = name,
        CardTypes = CardType.Creature,
        Power = 0,
        Toughness = 1,
        Keywords = KeywordAbility.Indestructible,
    };

    [Fact]
    public void Murder_does_not_kill_something_indestructible()
    {
        // CR 702.12b. The state-based actions already knew this about lethal damage; the
        // "destroy" effect did not, so removal killed a permanent that cannot be destroyed.
        var (game, alice, bob) = InMainPhase();
        var myr = game.Create(bob, Unkillable(), Zone.Battlefield);
        var murder = game.Create(alice, Card("Murder"), Zone.Hand);

        game.CastSpell(alice, murder, [Target.ToPermanent(myr)]);
        ResolveTop(game);

        Assert.Single(game.State.Battlefield);
        Assert.Empty(game.State.GetPlayer(bob).Graveyard);
    }

    [Fact]
    public void Scour_from_existence_exiles_a_permanent()
    {
        var (game, alice, bob) = InMainPhase();
        var bear = game.Create(bob, Vanilla("Bear", 2, 2), Zone.Battlefield);
        var scour = game.Create(alice, Card("Scour from Existence"), Zone.Hand);

        game.CastSpell(alice, scour, [Target.ToPermanent(bear)]);
        ResolveTop(game);

        Assert.Empty(game.State.Battlefield);
        Assert.Single(game.State.Exile);
        Assert.Empty(game.State.GetPlayer(bob).Graveyard);
    }

    [Fact]
    public void Scour_from_existence_answers_something_indestructible()
    {
        // The reason to print an exile effect at all: indestructible only protects against
        // being destroyed (CR 702.12b), and exile does not destroy.
        var (game, alice, bob) = InMainPhase();
        var myr = game.Create(bob, Unkillable(), Zone.Battlefield);
        var scour = game.Create(alice, Card("Scour from Existence"), Zone.Hand);

        game.CastSpell(alice, scour, [Target.ToPermanent(myr)]);
        ResolveTop(game);

        Assert.Empty(game.State.Battlefield);
        Assert.Single(game.State.Exile);
    }

    [Fact]
    public void Revitalize_gains_three_life_and_draws_a_card()
    {
        var (game, alice, _) = InMainPhase();
        var libraryBefore = game.State.GetPlayer(alice).Library.Count;
        var revitalize = game.Create(alice, Card("Revitalize"), Zone.Hand);

        game.CastSpell(alice, revitalize);
        ResolveTop(game);

        Assert.Equal(23, game.State.GetPlayer(alice).Life);
        Assert.Equal(libraryBefore - 1, game.State.GetPlayer(alice).Library.Count);
    }

    [Fact]
    public void Raise_the_alarm_makes_two_soldiers()
    {
        var (game, alice, _) = InMainPhase();
        var alarm = game.Create(alice, Card("Raise the Alarm"), Zone.Hand);

        game.CastSpell(alice, alarm);
        ResolveTop(game);

        var soldiers = game.State.Battlefield
            .Select(id => game.State.GetObject(id))
            .Where(o => string.Equals(o.Card.Name, "Soldier", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, soldiers.Count);
        Assert.All(soldiers, s => Assert.Equal(1, s.Card.Power));
    }

    [Fact]
    public void Bond_beetle_puts_a_counter_on_a_creature()
    {
        // CR 603.3d: a triggered ability's targets are chosen as it goes on the stack. Before
        // the engine asked, a targeting trigger reached the stack with no targets and resolved
        // into nothing — the counter simply never appeared, and no rule was reported broken.
        var (game, alice, _) = InMainPhase();
        var bear = game.Create(alice, Vanilla("Bear", 2, 2), Zone.Battlefield);
        var beetle = game.Create(
            alice, Card("Bond Beetle", CardType.Creature, text: null, 0, 1), Zone.Hand);

        game.CastSpell(alice, beetle);
        SettleAnsweringTargets(game, "object:" + bear.Value.ToString("N"));

        var counters = game.State.GetObject(bear).Permanent?.Counters;
        Assert.NotNull(counters);
        Assert.Equal(1, counters!.GetValueOrDefault(CounterKinds.PlusOnePlusOne));
    }

    [Fact]
    public void Bond_beetle_can_put_its_counter_on_itself()
    {
        // "Target creature" includes the beetle: it is on the battlefield by the time its own
        // enters-the-battlefield trigger goes on the stack (CR 603.6a).
        var (game, alice, _) = InMainPhase();
        var beetle = game.Create(
            alice, Card("Bond Beetle", CardType.Creature, text: null, 0, 1), Zone.Hand);

        game.CastSpell(alice, beetle);
        SettleAnsweringTargets(game, null);

        var onField = game.State.Battlefield.Single();
        Assert.Equal(
            1,
            game.State.GetObject(onField).Permanent!.Counters
                .GetValueOrDefault(CounterKinds.PlusOnePlusOne));
    }

    /// <summary>
    /// Plays out until nothing is waiting, answering a trigger's target question with
    /// <paramref name="preferred"/> when it is offered and the first option otherwise.
    /// </summary>
    private static void SettleAnsweringTargets(Game game, string? preferred)
    {
        for (var guard = 0; guard < 80; guard++)
        {
            if (game.State.Choice is { } choice)
            {
                var pick = preferred is not null
                    && choice.Options.Any(o => string.Equals(o.Id, preferred, StringComparison.Ordinal))
                        ? preferred
                        : choice.Options[0].Id;

                game.Choose(
                    choice.PlayerId,
                    choice.Kind == ChoiceKind.ChooseTriggerTargets
                        ? [pick]
                        : [.. choice.Options.Take(choice.MinPicks).Select(o => o.Id)]);
                continue;
            }

            if (game.State.Stack.IsEmpty && game.State.PendingTriggers.IsEmpty)
                return;

            if (game.State.Priority.Holder is not { } holder)
                return;

            game.PassPriority(holder);
        }
    }

    [Fact]
    public void Unsummon_returns_a_creature_to_its_owners_hand()
    {
        var (game, alice, bob) = InMainPhase();
        var handBefore = game.State.GetPlayer(bob).Hand.Count;
        var bear = game.Create(bob, Vanilla("Bear", 2, 2), Zone.Battlefield);
        var unsummon = game.Create(alice, Card("Unsummon"), Zone.Hand);

        game.CastSpell(alice, unsummon, [Target.ToPermanent(bear)]);
        ResolveTop(game);

        Assert.Empty(game.State.Battlefield);
        Assert.Equal(handBefore + 1, game.State.GetPlayer(bob).Hand.Count);
        Assert.Empty(game.State.GetPlayer(bob).Graveyard);
    }

    [Fact]
    public void Unsummon_answers_something_indestructible()
    {
        // Bouncing is not destroying, so indestructible does not stop it (CR 702.12b) — and
        // unlike exile the card comes back, which is what makes it the cheap answer.
        var (game, alice, bob) = InMainPhase();
        var myr = game.Create(bob, Unkillable(), Zone.Battlefield);
        var unsummon = game.Create(alice, Card("Unsummon"), Zone.Hand);

        game.CastSpell(alice, unsummon, [Target.ToPermanent(myr)]);
        ResolveTop(game);

        Assert.Empty(game.State.Battlefield);
        Assert.Contains(
            game.State.GetPlayer(bob).Hand,
            id => string.Equals(game.State.GetObject(id).Card.Name, "Darksteel Myr", StringComparison.Ordinal));
    }

    [Fact]
    public void Icy_manipulator_taps_a_creature()
    {
        var (game, alice, bob) = InMainPhase();
        var icy = game.Create(alice, Card("Icy Manipulator", CardType.Artifact), Zone.Battlefield);
        var bear = game.Create(bob, Vanilla("Bear", 2, 2), Zone.Battlefield);

        game.ActivateAbility(alice, icy, "tap", [Target.ToPermanent(bear)]);
        ResolveTop(game);

        Assert.True(game.State.GetObject(bear).Permanent!.IsTapped);
    }

    [Fact]
    public void Icy_manipulator_can_tap_a_land()
    {
        // "Target artifact, creature, or land" is why the spec is any permanent: the ability is
        // most often pointed at a land, to keep the mana from being spent.
        var (game, alice, bob) = InMainPhase();
        var icy = game.Create(alice, Card("Icy Manipulator", CardType.Artifact), Zone.Battlefield);
        var forest = game.Create(bob, BasicLand("Forest"), Zone.Battlefield);

        game.ActivateAbility(alice, icy, "tap", [Target.ToPermanent(forest)]);
        ResolveTop(game);

        Assert.True(game.State.GetObject(forest).Permanent!.IsTapped);
    }

    [Fact]
    public void Tapping_something_already_tapped_changes_nothing()
    {
        var (game, alice, bob) = InMainPhase();
        // Two of them, because the ability costs {T} and the first is tapped paying for itself
        // (CR 602.5b) — the engine refuses a second activation of the same one, correctly.
        var first = game.Create(alice, Card("Icy Manipulator", CardType.Artifact), Zone.Battlefield);
        var second = game.Create(alice, Card("Icy Manipulator", CardType.Artifact), Zone.Battlefield);
        var bear = game.Create(bob, Vanilla("Bear", 2, 2), Zone.Battlefield);

        game.ActivateAbility(alice, first, "tap", [Target.ToPermanent(bear)]);
        ResolveTop(game);
        var logAfterFirst = game.Log.Count;

        game.ActivateAbility(alice, second, "tap", [Target.ToPermanent(bear)]);
        ResolveTop(game);

        Assert.True(game.State.GetObject(bear).Permanent!.IsTapped);
        Assert.DoesNotContain(
            game.Log.Skip(logAfterFirst),
            e => e is PermanentTapped tapped && tapped.Id == bear);
    }

    [Fact]
    public void The_view_says_what_a_permanent_can_be_asked_to_do()
    {
        // A client cannot work this out. Everything the board knew about activating was the
        // word "mana" hardcoded against lands, so a Sol Ring could not be tapped and a Prodigal
        // Pyromancer could not be pointed at anything — both abilities existed and neither was
        // reachable.
        var (game, alice, _) = InMainPhase();
        game.Create(alice, Card("Sol Ring", CardType.Artifact), Zone.Battlefield);

        var view = game.ViewFor(alice);
        var solRing = view.Battlefield.Single(o => o.Name == "Sol Ring");

        var ability = Assert.Single(solRing.Abilities);
        Assert.Equal("mana", ability.Id);
        Assert.True(ability.RequiresTap);
        Assert.True(ability.IsManaAbility);
        Assert.Equal(0, ability.TargetCount);
    }

    [Fact]
    public void The_view_says_how_many_targets_an_ability_needs()
    {
        var (game, alice, _) = InMainPhase();
        game.Create(alice, Card("Icy Manipulator", CardType.Artifact), Zone.Battlefield);

        var icy = game.ViewFor(alice).Battlefield.Single(o => o.Name == "Icy Manipulator");
        var ability = Assert.Single(icy.Abilities);

        Assert.Equal(1, ability.TargetCount);
        Assert.False(ability.IsManaAbility);
        Assert.Contains("Tap target", ability.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_permanent_with_nothing_to_activate_offers_nothing()
    {
        var (game, alice, _) = InMainPhase();
        game.Create(alice, Vanilla("Bear", 2, 2), Zone.Battlefield);

        var bear = game.ViewFor(alice).Battlefield.Single(o => o.Name == "Bear");

        Assert.Empty(bear.Abilities);
    }

    /// <summary>A creature with a {T} ability, for the summoning-sickness rules.</summary>
    private static CardDefinition Pinger(string name = "Prodigal Pyromancer", bool haste = false) =>
        new()
        {
            OracleId = "oracle-" + name.ToLowerInvariant().Replace(' ', '-'),
            Name = name,
            CardTypes = CardType.Creature,
            Power = 1,
            Toughness = 1,
            Keywords = haste ? KeywordAbility.Haste : KeywordAbility.None,
        };

    [Fact]
    public void A_creature_cannot_use_its_tap_ability_the_turn_it_arrives()
    {
        // CR 302.6 covers more than attacking: a creature's {T} ability is off limits too.
        var (game, alice, bob) = InMainPhase();
        var pinger = game.Create(alice, Pinger(), Zone.Battlefield);
        var bear = game.Create(bob, Vanilla("Bear", 2, 2), Zone.Battlefield);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            game.ActivateAbility(alice, pinger, "ping", [Target.ToPermanent(bear)]));

        Assert.Contains("302.6", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_creature_with_haste_can_use_its_tap_ability_at_once()
    {
        // CR 702.10c. The check knew about creatures and about {T}, and not about haste, so a
        // hasty pinger was refused an ability the rules give it.
        var (game, alice, bob) = InMainPhase();
        var pinger = game.Create(alice, Pinger(haste: true), Zone.Battlefield);
        var bear = game.Create(bob, Vanilla("Bear", 2, 2), Zone.Battlefield);

        game.ActivateAbility(alice, pinger, "ping", [Target.ToPermanent(bear)]);
        ResolveTop(game);

        Assert.True(game.State.GetObject(pinger).Permanent!.IsTapped);
        Assert.Equal(1, game.State.GetObject(bear).Permanent!.DamageMarked);
    }

    [Fact]
    public void A_noncreature_permanent_taps_the_turn_it_arrives()
    {
        // CR 302.6 is about creatures only — a Sol Ring makes mana the turn it lands.
        var (game, alice, _) = InMainPhase();
        var solRing = game.Create(alice, Card("Sol Ring", CardType.Artifact), Zone.Battlefield);

        game.ActivateAbility(alice, solRing, "mana");

        Assert.Equal(2, game.State.GetPlayer(alice).ManaPool.Colorless);
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
                game.DeclareAttackers(game.State.ActivePlayerId, new Dictionary<ObjectId, AttackTarget>());
                continue;
            }

            game.PassPriority(game.State.Priority.Holder!.Value);
        }
    }
}
