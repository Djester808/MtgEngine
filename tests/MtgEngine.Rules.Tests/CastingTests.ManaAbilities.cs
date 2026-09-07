using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using AbilityActivated = MtgEngine.Rules.Events.AbilityActivated;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A mana ability that does more than add mana, CR 605.1a and 605.3a: it is still a mana ability,
/// so it resolves as it is activated, without the stack, and everything else it says happens in
/// the same breath — through the real interpreter, so the walk is the one a stack entry would
/// have had.
/// </summary>
public sealed class CastingManaAbilityTests
{
    private readonly CastingSystem casting;

    public CastingManaAbilityTests()
    {
        var chars = new PrintedCharacteristics();
        var zones = new CastingFakeZones();
        var query = new Query.Query(chars);
        casting = new CastingSystem(zones, query, chars, new EffectInterpreter(query, zones, new CastingPassThroughReplacements(), chars));
    }

    [Fact]
    [RuleRef("605.3a")]
    public void A_mana_ability_that_does_more_resolves_as_it_is_activated_without_the_stack()
    {
        // CR 605.1a: "add one mana of any color; when that mana is spent, ...; you gain 1 life"
        // could add mana, targets nothing and is no loyalty ability, so it is a mana ability
        // whatever else it does. CR 605.3a: it resolves at once and never uses the stack — the
        // pool holds the announced colour, the delayed trigger exists, the life was gained, and
        // the activation event says there was no stack object.
        var table = new CastingTable();
        var gem = table.Add(CastingManaAbilityCards.AnyColorGemThatRemembersAndGainsLife(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        Assert.Null(casting.Check(table.State, new ActivateAbility(CastingTable.A, gem, 0, ManaColors: "U")));
        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, gem, 0, ManaColors: "U"));

        Assert.True(StackZone.IsEmpty(activated.State));
        var pool = activated.State.Player(CastingTable.A).Mana;
        Assert.Equal(1, pool.Blue);
        Assert.Equal(1, pool.Total);
        Assert.Equal(21, activated.State.Player(CastingTable.A).Life);
        Assert.Single(activated.State.DelayedTriggers);
        Assert.True(activated.State.Objects[gem].Tapped);

        var events = activated.Events;
        Assert.Null(Assert.Single(events.OfType<AbilityActivated>()).StackObject);
        Assert.Single(events.OfType<DelayedTriggerCreated>());
        Assert.Single(events.OfType<LifeChanged>());

        // The cost's events, then the activation, then what the ability did — CR 602.2 and 605.3a.
        var tapped = events.FindIndex(e => e is ObjectChanged { Tapped: true });
        var announced = events.FindIndex(e => e is AbilityActivated);
        var added = events.FindIndex(e => e is ManaChanged);
        Assert.True(tapped >= 0 && tapped < announced, "the cost comes before the activation");
        Assert.True(announced < added, "the mana follows the activation");
    }

    [Fact]
    [RuleRef("605.1a")]
    public void A_sequence_nested_in_a_sequence_that_adds_mana_is_still_a_mana_ability()
    {
        // CR 605.1a asks whether the ability could add mana as it resolves, not where in its text
        // the adding is written, so a step inside a step counts and the stack is never used.
        var table = new CastingTable();
        var gem = table.Add(CastingManaAbilityCards.NestedAnyColorGem(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, gem, 0, ManaColors: "W"));

        Assert.True(StackZone.IsEmpty(activated.State));
        Assert.Equal(1, activated.State.Player(CastingTable.A).Mana.White);
        Assert.Equal(21, activated.State.Player(CastingTable.A).Life);
    }

    [Fact]
    [RuleRef("605.3a")]
    public void A_mana_ability_that_would_stop_to_ask_is_refused()
    {
        // CR 605.3a: a mana ability resolves immediately and cannot be responded to, so there is no
        // moment at which the game may stand still on a question it asks. A "you may" inside one
        // is refused naming the rule rather than leaving the game pending with mana half-added.
        var table = new CastingTable();
        var gem = table.Add(CastingManaAbilityCards.AnyColorGemThatAsks(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        Assert.Equal("605.3a", casting.Check(table.State, new ActivateAbility(CastingTable.A, gem, 0, ManaColors: "R"))?.Rule);
        Assert.Null(table.State.Pending);
    }

    [Fact]
    [RuleRef("608.2d")]
    public void A_mana_ability_that_does_more_without_a_colour_is_refused_by_its_resolution()
    {
        // CR 608.2d: the colour is chosen as the mana is added, and on the immediate road that is
        // the interpreter's own refusal reaching the activation — a choice not made is not guessed.
        var table = new CastingTable();
        var gem = table.Add(CastingManaAbilityCards.AnyColorGemThatRemembersAndGainsLife(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        Assert.Equal("608.2d", casting.Check(table.State, new ActivateAbility(CastingTable.A, gem, 0))?.Rule);
    }
}

/// <summary>CR 616 is not under test here: every event proposed happens as proposed.</summary>
internal sealed class CastingPassThroughReplacements : IReplacementSystem
{
    public string Name => "casting pass-through replacements";

    public Outcome Replace(GameState state, GameEvent proposed, PlayerId affected) =>
        new(proposed.Apply(state), [proposed]);
}

/// <summary>
/// Cards as data for <see cref="CastingManaAbilityTests"/>, built here rather than read from
/// <c>cards/</c>; the names are deliberately not any printed card's.
/// </summary>
internal static class CastingManaAbilityCards
{
    private const string ManaKey = "gem";

    /// <summary>"{T}: Add one mana of any color. When that mana is spent, you gain 1 life. You gain 1 life", CR 605.1a.</summary>
    public static CardDefinition AnyColorGemThatRemembersAndGainsLife() => Gem(
        "CASTING MANA ABILITY GEM",
        "Casting Mana Ability Gem",
        new Sequence(
        [
            new Cards.AddMana(new ManaProduction(AnyColor: true), Key: ManaKey),
            new DelayedTrigger(new Cards.ManaSpent(ManaKey), new GainLife(new Fixed(1))),
            new GainLife(new Fixed(1)),
        ]));

    /// <summary>The same adding, written one sequence deeper: CR 605.1a reads the tree, not its top.</summary>
    public static CardDefinition NestedAnyColorGem() => Gem(
        "CASTING MANA ABILITY NESTED GEM",
        "Casting Mana Ability Nested Gem",
        new Sequence(
        [
            new Sequence([new Cards.AddMana(new ManaProduction(AnyColor: true))]),
            new GainLife(new Fixed(1)),
        ]));

    /// <summary>"{T}: Add one mana of any color. You may gain 1 life": a question no mana ability may stop on, CR 605.3a.</summary>
    public static CardDefinition AnyColorGemThatAsks() => Gem(
        "CASTING MANA ABILITY ASKING GEM",
        "Casting Mana Ability Asking Gem",
        new Sequence(
        [
            new Cards.AddMana(new ManaProduction(AnyColor: true)),
            new May(new GainLife(new Fixed(1))),
        ]));

    private static CardDefinition Gem(string oracleId, string name, Effect does) => new(
        oracleId,
        name,
        Layout.Normal,
        [
            new Face(
                name,
                FaceRole.Main,
                new TypeLine(CardType.Artifact),
                "{1}",
                Abilities: [new ActivatedAbility([new TapSelf()], does)]),
        ]);
}
