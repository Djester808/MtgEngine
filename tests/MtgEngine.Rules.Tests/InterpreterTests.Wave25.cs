using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 25: a change of targets whose own target carries a key (CR 115.7 with 601.2c), and the
/// mana value of the spell that key remembers (CR 202.3, 608.2h).
/// </summary>
public sealed partial class InterpreterTests
{
    /// <summary>
    /// The shape the corpus asks for: "change the target of target spell with a single target. You
    /// lose life equal to that spell's mana value" — one target selector, keyed, read twice.
    /// </summary>
    private static Effect KeyedRetargetAndPay(string key) => new Sequence(
    [
        new ChangeTarget(new Target(new ObjectFilter(In: Zone.Stack, Spell: true), Key: key)),
        new LoseLife(new ManaValueOf(new Remembered(key))),
    ]);

    [Fact]
    [RuleRef("115.7")]
    public void A_retarget_whose_own_target_carries_a_key_still_changes_the_spell()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.B);
        var bolt = KeyedRetargetBolt(table, creature);

        var mischief = table.OnStack(
            table.A,
            KeyedRetargetAndPay("k1"),
            new StackEntry(null, null, [bolt], [], [], null, false));
        table.Resolve(mischief);

        // The question is asked at all: without the keyed announcement being read back the walk
        // finds no spell and the whole ability plays as nothing (CR 115.7).
        Assert.Equal("target", table.Pending.Kind);
        var events = table.Answer("player:0");
        Assert.Equal(table.A, Assert.Single(Assert.Single(events.OfType<StackTargetsChanged>()).TargetPlayers));
    }

    [Fact]
    [RuleRef("202.3")]
    public void A_keyed_stack_object_reads_its_mana_value()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.B);
        var bolt = KeyedRetargetBolt(table, creature);

        var mischief = table.OnStack(
            table.A,
            KeyedRetargetAndPay("k1"),
            new StackEntry(null, null, [bolt], [], [], null, false));
        table.Resolve(mischief);
        table.Answer("player:0");

        // CR 202.3 with 608.2h: the spell the key remembered is still on the stack, and its mana
        // value is {2}{R} — three. The life is what proves the second step ran at all.
        Assert.Equal(17, table.Life(table.A));
    }

    [Fact]
    [RuleRef("112.1")]
    public void A_spell_filter_that_names_the_stack_is_read_by_the_re_check()
    {
        // The CR 608.2b re-check runs the selector's own filter over the announced target, so a
        // filter that admits the spell is what lets every later step read the key. The card's own
        // filter says only "spell" and names no zone, and CR 112.1 makes that the stack — the query
        // reads it as the battlefield, where nothing is ever a spell, so the whole ability plays as
        // nothing. That half is asked for in seam-requests/E9t.md; this is the road once it lands.
        var table = new InterpreterTable();
        var creature = table.Permanent(table.B);
        var bolt = KeyedRetargetBolt(table, creature);
        var negate = table.OnStack(
            table.A,
            new Cards.Counter(new Target(new ObjectFilter(In: Zone.Stack, Spell: true))),
            new StackEntry(null, null, [bolt], [], [], null, false));

        table.Resolve(negate);

        // CR 701.6a with 400.7: the countered spell is its owner's card in their graveyard, and it
        // is a new object there, so what is asserted is that the graveyard filled at all.
        Assert.NotEmpty(table.In(Zone.Graveyard, table.B));
    }

    /// <summary>A spell on the stack with a mana cost to read and one target to move.</summary>
    private static ObjectId KeyedRetargetBolt(InterpreterTable table, ObjectId at)
    {
        var bolt = table.OnStack(
            table.B,
            new DealDamage(new Fixed(3), new AnyTarget()),
            new StackEntry(null, null, [at], [], [], null, false));
        table.Layers.Set(bolt, new Characteristics(
            "engine test object",
            CardType.Instant,
            Supertype.None,
            [],
            Color.Red,
            null,
            null,
            null,
            null,
            "{2}{R}",
            [],
            table.B));
        return bolt;
    }
}
