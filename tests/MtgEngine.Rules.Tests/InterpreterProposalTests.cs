using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 616.1 raised from inside a resolution: the walk stops, and the answer puts it back.
///
/// Both halves were missing. The walk did not stop, so the instructions written after the
/// proposal ran while the proposed event had not happened yet and the answer applied it to a board
/// that had moved on. Stopping alone was worse: the replacement layer's own question names no
/// source, no ability and no path (see <c>ReplacementSystem.Ask</c>), so nothing could find the
/// stopped walk again and every instruction after the proposal was dropped for ever. The question
/// is now asked by the walk, carries the walk's coordinates, and the node that proposed the event
/// is the one the answer comes back to.
/// </summary>
public sealed class InterpreterProposalTests
{
    private static InterpreterTable Asking(Func<GameEvent, bool> about)
    {
        var table = new InterpreterTable();
        table.Replacements.Asks = about;
        return table;
    }

    [Fact]
    [RuleRef("616.1")]
    public void A_replacement_question_stops_the_walk_before_the_event_happens()
    {
        var table = Asking(e => e is LifeChanged);
        var spell = table.OnStack(table.A, new Sequence([new GainLife(new Fixed(3)), new Draw(new Fixed(1))]));

        table.Resolve(spell);

        Assert.Equal(ChoiceKinds.ReplacementOrder, table.Pending.Kind);
        Assert.Equal(20, table.Life(table.A));
    }

    /// <summary>
    /// The defect this file was written for. The draw is written after the life gain, and it is the
    /// answer that lets the life gain happen; a road that finished the event without going back to
    /// the walk left the draw undone with nothing to do it.
    /// </summary>
    [Fact]
    [RuleRef("101.4")]
    public void The_instructions_after_the_proposal_still_run_once_the_question_is_answered()
    {
        var table = Asking(e => e is LifeChanged);
        var before = table.In(Zone.Hand, table.A).Count;
        table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Sequence([new GainLife(new Fixed(3)), new Draw(new Fixed(1))]));

        table.Resolve(spell);
        table.Answer("first");

        Assert.Equal(23, table.Life(table.A));
        Assert.Equal(before + 1, table.In(Zone.Hand, table.A).Count);
    }

    /// <summary>The question has to say which walk it stopped, or nothing can put that walk back (CR 101.4).</summary>
    [Fact]
    [RuleRef("101.4")]
    public void The_question_carries_the_walk_that_raised_it()
    {
        var table = Asking(e => e is LifeChanged);
        var spell = table.OnStack(table.A, new Sequence([new GainLife(new Fixed(3)), new Draw(new Fixed(1))]));

        table.Resolve(spell);

        Assert.NotNull(table.Pending.Context.EffectPath);
        Assert.NotNull(table.Pending.Context.Event);
    }

    /// <summary>
    /// CR 616.1 with 608.2: the node that stopped runs again from its start, and a node that
    /// proposes one event per player would offer the earlier players' events a second time. Each
    /// player gains three life once, not twice.
    /// </summary>
    [Fact]
    [RuleRef("608.2")]
    public void A_node_that_proposes_once_per_player_does_not_propose_twice()
    {
        // Only the second player's life change is contested, so the walk stops with the first
        // player's already applied — exactly the state a resumed node must not redo.
        var table = Asking(e => e is LifeChanged { Player.Value: 1 });
        var spell = table.OnStack(table.A, new GainLife(new Fixed(3), new EachPlayer()));

        table.Resolve(spell);
        Assert.Equal(23, table.Life(table.A));
        Assert.Equal(20, table.Life(table.B));

        table.Answer("first");

        Assert.Equal(23, table.Life(table.A));
        Assert.Equal(23, table.Life(table.B));
    }

    /// <summary>The event the question was about happens exactly once, through the answer (CR 616.1).</summary>
    [Fact]
    [RuleRef("616.1")]
    public void The_event_the_question_was_about_happens_once()
    {
        var table = Asking(e => e is LifeChanged);
        var spell = table.OnStack(table.A, new GainLife(new Fixed(3)));

        table.Resolve(spell);
        var events = table.Answer("first");

        Assert.Single(events, e => e is LifeChanged);
        Assert.Equal(23, table.Life(table.A));
    }
}
