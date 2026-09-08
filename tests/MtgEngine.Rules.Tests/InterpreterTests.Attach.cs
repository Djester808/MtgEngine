using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 701.3, attaching, and the refusal that stands where an arm does not. "Attach it to target
/// legendary creature you control" is an Equipment moving onto a host without changing zones, and
/// the same instruction aimed at something the rules do not admit does nothing at all (CR 701.3b)
/// rather than crashing the resolution.
/// </summary>
public sealed partial class InterpreterTests
{
    [Fact]
    [RuleRef("701.3a")]
    public void An_equipment_the_arm_attaches_reaches_the_creature_it_names()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.A);
        var equipment = AttachFixture(table, table.A, CardType.Artifact, "Equipment");

        var events = table.Run(
            new Attach(new Self(), new Target(new ObjectFilter(Types: CardType.Creature))),
            table.Context(equipment) with { Targets = [creature] });

        // CR 303.4f's event, the one the casting system writes for an Aura entering attached.
        Assert.Equal(creature, table.Object(equipment).AttachedTo);
        Assert.Contains(events, e => e is ObjectChanged { AttachedTo: not null });

        // CR 701.3a: it is put onto the object, not moved to another zone, and it is not a new
        // object — the same id is still the permanent on the battlefield.
        Assert.Equal(Zone.Battlefield, table.Object(equipment).Zone.Zone);
    }

    [Fact]
    [RuleRef("701.3a")]
    public void An_aura_the_arm_attaches_reaches_the_permanent_its_enchant_ability_admits()
    {
        // The other spelling of the same keyword action: AttachAction is the CR 701 record and
        // Attach the verb, and they mean one thing, so both reach the one arm.
        var table = new InterpreterTable();
        var creature = table.Permanent(table.A);
        var aura = AttachFixture(
            table,
            table.A,
            CardType.Enchantment,
            "Aura",
            new KeywordAbility(KeywordKind.Enchant, Of: new ObjectFilter(Types: CardType.Creature)));

        table.Run(
            new AttachAction(new Self(), new Target(new ObjectFilter(Types: CardType.Creature))),
            table.Context(aura) with { Targets = [creature] });

        Assert.Equal(creature, table.Object(aura).AttachedTo);
        Assert.Null(table.Object(aura).AttachedToPlayer);
    }

    [Fact]
    [RuleRef("701.3b")]
    public void An_equipment_aimed_at_a_permanent_that_is_not_a_creature_stays_where_it_was()
    {
        // CR 301.5c: an Equipment is attached only to a creature, so CR 701.3b's "nothing happens"
        // is the whole of the instruction — and the walk carries on past it.
        var table = new InterpreterTable();
        var land = table.Permanent(table.A, CardType.Land, power: null, toughness: null);
        var equipment = AttachFixture(table, table.A, CardType.Artifact, "Equipment");

        var events = table.Run(
            new Sequence([new Attach(new Self(), new Target(new ObjectFilter())), new GainLife(new Fixed(3))]),
            table.Context(equipment) with { Targets = [land] });

        Assert.Null(table.Object(equipment).AttachedTo);
        Assert.DoesNotContain(events, e => e is ObjectChanged { AttachedTo: not null });
        Assert.Equal(23, table.Life(table.A));
    }

    [Fact]
    [RuleRef("701.3b")]
    public void An_aura_aimed_at_a_permanent_its_enchant_ability_refuses_stays_where_it_was()
    {
        // CR 702.5b: "enchant creature" is what a legal host means for this Aura, and a land is
        // not one. The reading is the same one CR 704.5m makes of an Aura already attached.
        var table = new InterpreterTable();
        var land = table.Permanent(table.A, CardType.Land, power: null, toughness: null);
        var aura = AttachFixture(
            table,
            table.A,
            CardType.Enchantment,
            "Aura",
            new KeywordAbility(KeywordKind.Enchant, Of: new ObjectFilter(Types: CardType.Creature)));

        table.Run(
            new Attach(new Self(), new Target(new ObjectFilter())),
            table.Context(aura) with { Targets = [land] });

        Assert.Null(table.Object(aura).AttachedTo);
    }

    [Fact]
    [RuleRef("701.3b")]
    public void A_permanent_that_is_none_of_the_three_kinds_is_never_attached()
    {
        // CR 701.3a names an Aura, an Equipment and a Fortification. A plain artifact has nothing
        // it can legally be attached to, so the instruction does nothing rather than inventing one.
        var table = new InterpreterTable();
        var creature = table.Permanent(table.A);
        var artifact = AttachFixture(table, table.A, CardType.Artifact);

        table.Run(
            new Attach(new Self(), new Target(new ObjectFilter())),
            table.Context(artifact) with { Targets = [creature] });

        Assert.Null(table.Object(artifact).AttachedTo);
    }

    [Fact]
    [RuleRef("608.2")]
    public void An_effect_with_no_interpreter_arm_is_refused_and_never_thrown_as_a_crash()
    {
        // The dispatch fallback used to be a NotImplementedException, which crashes the game rather
        // than telling the caller what the engine cannot do. CR 608.2: the resolution is refused by
        // the effect's own vocabulary name, and the rule the refusal cites is this one.
        var table = new InterpreterTable();

        var refused = Assert.Throws<RefusedException>(() => table.Run(new AttachUnbuiltEffect()));

        Assert.Equal("608.2", refused.Rule);
        Assert.Contains(nameof(AttachUnbuiltEffect), refused.Reason, StringComparison.Ordinal);
    }

    /// <summary>An effect no arm claims: what the dispatch fallback is asked about.</summary>
    private sealed record AttachUnbuiltEffect : Effect;

    /// <summary>
    /// A permanent on the battlefield whose subtypes and abilities the test sets, which is what
    /// attaching reads: the subtype says which of CR 701.3a's three kinds it is, and an Aura's
    /// enchant ability says what it may be attached to.
    /// </summary>
    private static ObjectId AttachFixture(
        InterpreterTable table,
        PlayerId controller,
        CardType types,
        string? subtype = null,
        params Ability[] abilities)
    {
        var id = table.Put(controller, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(types));
        table.Layers.Set(id, new Characteristics(
            "engine test object",
            types,
            Supertype.None,
            subtype is null ? [] : [subtype],
            Color.Colorless,
            null,
            null,
            null,
            null,
            null,
            [.. abilities],
            controller));
        return id;
    }
}
