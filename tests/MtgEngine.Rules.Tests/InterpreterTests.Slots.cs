using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 601.2c with 608.2b: the targets were announced in the order the tree names its target
/// selectors, so each selector reads the slot that was filled for it — the second unkeyed
/// <see cref="Target"/> reads the second announced object, not the whole list. A keyed selector
/// reads its key as before, a single unkeyed target reads the one announced object as before, and
/// a slot past what was announced reads nothing.
/// </summary>
public sealed partial class InterpreterTests
{
    private static readonly ObjectFilter E9nMine = new(Types: CardType.Creature, ControlledBy: new You());

    private static readonly ObjectFilter E9nTheirs = new(Types: CardType.Creature, ControlledBy: new EachOpponent());

    private static StackEntry E9nAimedAt(params ObjectId[] targets) => new(null, null, [.. targets], [], [], null, false);

    [Fact]
    [RuleRef("601.2c")]
    public void A_fight_between_two_unkeyed_targets_reads_each_creature_from_its_own_slot()
    {
        var table = new InterpreterTable();
        var mine = table.Permanent(table.A, power: 6, toughness: 6);
        var theirs = table.Permanent(table.B, power: 2, toughness: 2);
        var spell = table.OnStack(
            table.A,
            new Fight(new Target(E9nMine), new Target(E9nTheirs)),
            E9nAimedAt(mine, theirs));

        table.Resolve(spell);

        // CR 701.14a: the first slot's creature fights the second slot's, not itself.
        Assert.Equal(6, table.Object(theirs).Damage);
        Assert.Equal(2, table.Object(mine).Damage);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void Two_unkeyed_targets_in_sequence_each_read_only_their_own_announced_object()
    {
        var table = new InterpreterTable();
        var first = table.Permanent(table.A, power: 1, toughness: 5);
        var second = table.Permanent(table.B, power: 1, toughness: 5);
        var spell = table.OnStack(
            table.A,
            new Sequence(
            [
                new DealDamage(new Fixed(3), To: new Target(ObjectFilter.Creature)),
                new DealDamage(new Fixed(1), To: new Target(ObjectFilter.Creature)),
            ]),
            E9nAimedAt(first, second));

        table.Resolve(spell);

        Assert.Equal(3, table.Object(first).Damage);
        Assert.Equal(1, table.Object(second).Damage);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void A_single_unkeyed_target_still_reads_the_one_announced_object()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.B, power: 1, toughness: 5);
        var spell = table.OnStack(
            table.A,
            new DealDamage(new Fixed(2), To: new Target(ObjectFilter.Creature)),
            E9nAimedAt(creature));

        table.Resolve(spell);

        Assert.Equal(2, table.Object(creature).Damage);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void A_keyed_target_is_still_read_under_its_key()
    {
        var table = new InterpreterTable();
        var first = table.Permanent(table.A, power: 1, toughness: 5);
        var second = table.Permanent(table.B, power: 1, toughness: 5);
        var spell = table.OnStack(
            table.A,
            new Sequence(
            [
                new DealDamage(new Fixed(3), To: new Target(ObjectFilter.Creature, Key: "a")),
                new DealDamage(new Fixed(1), To: new Target(ObjectFilter.Creature, Key: "b")),
                new DealDamage(new Fixed(1), To: new Remembered("a")),
            ]),
            E9nAimedAt(first, second));

        table.Resolve(spell);

        // CR 608.2h: "a" is the first announced object and a later step reads it by that key.
        Assert.Equal(4, table.Object(first).Damage);
        Assert.Equal(1, table.Object(second).Damage);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void A_keyed_target_beside_an_unkeyed_one_each_keep_their_slot()
    {
        var table = new InterpreterTable();
        var first = table.Permanent(table.A, power: 1, toughness: 5);
        var second = table.Permanent(table.B, power: 1, toughness: 5);
        var spell = table.OnStack(
            table.A,
            new Sequence(
            [
                new DealDamage(new Fixed(3), To: new Target(ObjectFilter.Creature, Key: "a")),
                new DealDamage(new Fixed(1), To: new Target(ObjectFilter.Creature)),
            ]),
            E9nAimedAt(first, second));

        table.Resolve(spell);

        Assert.Equal(3, table.Object(first).Damage);
        Assert.Equal(1, table.Object(second).Damage);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void A_slot_past_the_announced_targets_reads_nothing()
    {
        var table = new InterpreterTable();
        var mine = table.Permanent(table.A, power: 6, toughness: 6);
        var spell = table.OnStack(
            table.A,
            new Fight(new Target(E9nMine), new Target(E9nTheirs)),
            E9nAimedAt(mine));

        table.Resolve(spell);

        // The second slot was never filled, so there is no second creature and nothing fights.
        Assert.Equal(0, table.Object(mine).Damage);
    }

    [Fact]
    [RuleRef("608.2b")]
    public void A_target_that_became_illegal_is_dropped_from_its_slot_and_the_other_slot_is_kept()
    {
        var table = new InterpreterTable();
        var first = table.Permanent(table.A, power: 1, toughness: 5);
        var second = table.Permanent(table.B, power: 1, toughness: 5);
        var spell = table.OnStack(
            table.A,
            new Sequence(
            [
                new DealDamage(new Fixed(3), To: new Target(E9nMine)),
                new DealDamage(new Fixed(1), To: new Target(E9nTheirs)),
            ]),
            E9nAimedAt(first, second));

        // CR 608.2b: the first target is no longer a creature, so the spell does nothing to it and
        // the second slot is still the second announced object, not shifted into the first.
        table.Layers.Set(first, new Characteristics("engine test object", CardType.Artifact, Supertype.None, [], Color.Colorless, null, null, null, null, null, [], table.A));
        table.Resolve(spell);

        Assert.Equal(0, table.Object(first).Damage);
        Assert.Equal(1, table.Object(second).Damage);
    }
}
