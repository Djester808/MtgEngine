using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 616.1: every event a card's effect would make is offered to the replacement and prevention
/// effects that would change it, before it happens. These tests hold the interpreter to that for
/// the events it makes itself — life, counters, tapping and the named actions — with a fake
/// replacement layer standing in for the real one, so what is under test is that the interpreter
/// offers the event and uses what comes back, not how the replacement system decides.
/// </summary>
public sealed class InterpreterProposalTests
{
    /// <summary>CR 614.1: "if you would gain life, you gain twice that much instead."</summary>
    [Fact]
    public void Life_gained_through_a_doubling_replacement_gains_double()
    {
        var table = new InterpreterTable();
        table.Replacements.Instead = e => e is LifeChanged { Delta: > 0 } gain ? gain with { Delta = gain.Delta * 2 } : e;

        var spell = table.OnStack(table.A, new GainLife(new Fixed(3)));
        table.Resolve(spell);

        Assert.Equal(26, table.Life(table.A));
        Assert.Contains(table.Replacements.Proposed, e => e is LifeChanged { Delta: 3 });
    }

    /// <summary>CR 614.1: "if one or more counters would be put on a creature, that many plus one are put on it instead."</summary>
    [Fact]
    public void Counters_put_through_a_one_more_replacement_put_one_more()
    {
        var table = new InterpreterTable();
        table.Replacements.Instead = e => e is CountersChanged { Delta: > 0 } put ? put with { Delta = put.Delta + 1 } : e;
        var creature = table.Permanent(table.A);

        var spell = table.OnStack(table.A, new PutCounters(new Each(ObjectFilter.Creature), "+1/+1", new Fixed(1)));
        table.Resolve(spell);

        Assert.Equal(2, table.Object(creature).Counters["+1/+1"]);
    }

    /// <summary>CR 614.1: a permanent that can't become tapped is not tapped by an effect that says to tap it.</summary>
    [Fact]
    public void A_tap_the_replacement_system_drops_taps_nothing()
    {
        var table = new InterpreterTable();
        table.Replacements.Instead = e => e is ObjectChanged { Tapped: true } ? null : e;
        var creature = table.Permanent(table.A);

        var spell = table.OnStack(table.A, new Tap(new Each(ObjectFilter.Creature)));
        var events = table.Resolve(spell);

        Assert.False(table.Object(creature).Tapped);
        Assert.DoesNotContain(events, e => e is ObjectChanged);
        Assert.Contains(table.Replacements.Proposed, e => e is ObjectChanged { Tapped: true });
    }

    /// <summary>CR 615.1: prevented life loss is not lost, and nothing happened for a trigger to watch.</summary>
    [Fact]
    public void Life_loss_the_replacement_system_drops_loses_nothing_and_emits_no_event()
    {
        var table = new InterpreterTable();
        table.Replacements.Instead = e => e is LifeChanged { Delta: < 0 } ? null : e;

        var spell = table.OnStack(table.A, new LoseLife(new Fixed(5)));
        var events = table.Resolve(spell);

        Assert.Equal(20, table.Life(table.A));
        Assert.DoesNotContain(events, e => e is LifeChanged);
    }

    /// <summary>
    /// CR 701.8b and 614.1: destroying is a named action and is offered before it happens, so an
    /// effect that replaces it — regeneration, "exile it instead" — stops the zone change too.
    /// </summary>
    [Fact]
    public void A_named_action_the_replacement_system_drops_does_not_change_zones()
    {
        var table = new InterpreterTable();
        table.Replacements.Instead = e => e is ObjectLeftBy { Verb: "destroyed" } ? null : e;
        var creature = table.Permanent(table.A);

        var spell = table.OnStack(table.A, new Destroy(new Each(ObjectFilter.Creature)));
        table.Resolve(spell);

        Assert.True(table.State.Objects.ContainsKey(creature));
        Assert.Equal(Zone.Battlefield, table.Object(creature).Zone.Zone);
        Assert.Contains(table.Replacements.Proposed, e => e is ObjectLeftBy { Verb: "destroyed" });
    }

    /// <summary>
    /// CR 101.4: a loop that stopped to ask starts again at the iteration it stopped at, over the
    /// objects it was iterating. The first creature is destroyed before the question is raised, so
    /// re-resolving "each creature" on the answer would put a different creature at the index the
    /// question named — the second would be skipped and the third asked about instead.
    /// </summary>
    [Fact]
    public void A_loop_that_asked_on_its_second_object_resumes_there_and_finishes_the_third()
    {
        var table = new InterpreterTable();
        var first = table.Permanent(table.A);
        var second = table.Permanent(table.A);
        var third = table.Permanent(table.A);
        table.SetTapped(second, true);

        // Each creature: a tapped one, its controller may gain 4 life; any other is destroyed.
        var spell = table.OnStack(table.A, new ForEach(
            new Each(ObjectFilter.Creature),
            new If(
                new IsTapped(new Triggering()),
                new May(new GainLife(new Fixed(4))),
                new Destroy(new Triggering()))));
        table.Resolve(spell);

        Assert.Equal("may", table.Pending.Kind);
        Assert.False(table.State.Objects.ContainsKey(first));

        table.Answer("yes");

        Assert.Equal(24, table.Life(table.A));
        Assert.True(table.State.Objects.ContainsKey(second));
        Assert.False(table.State.Objects.ContainsKey(third));
    }

    /// <summary>
    /// CR 101.4 and 614.1a: a question raised by a replacement's own instruction has no stack
    /// object, and is found again through the source and the ability index the question carried.
    /// </summary>
    [Fact]
    public void A_may_inside_a_replacements_instruction_resumes_and_finishes()
    {
        var table = new InterpreterTable();
        var with = new May(new GainLife(new Fixed(4)));
        var source = table.Put(
            table.A,
            ZoneKey.Shared(Zone.Battlefield),
            InterpreterTable.Definition(CardType.Enchantment, new ReplacementAbility(new WouldGainLife(new You()), with)));

        table.RunFrom(with, table.Context(source), abilityIndex: 0);

        Assert.Equal("may", table.Pending.Kind);
        Assert.Equal(source, table.Pending.Context.Source);
        Assert.Equal(0, table.Pending.Context.AbilityIndex);

        table.Answer("yes");

        Assert.Equal(24, table.Life(table.A));
    }

    /// <summary>CR 101.4: a question whose effect cannot be found again is refused, not thrown out of the engine.</summary>
    [Fact]
    public void A_question_whose_effect_cannot_be_found_again_is_refused()
    {
        var table = new InterpreterTable();
        var source = table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment));

        table.RunFrom(new May(new GainLife(new Fixed(4))), table.Context(source), abilityIndex: 7);

        var refused = Assert.Throws<RefusedException>(() => table.Answer("yes"));
        Assert.Equal("101.4", refused.Rule);
    }
}
