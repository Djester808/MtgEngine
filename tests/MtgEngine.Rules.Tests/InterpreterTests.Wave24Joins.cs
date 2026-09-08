using MtgEngine.Cards;
using MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Tests;

/// <summary>The wave-24 joins: the search arm writes the event a Searches trigger watches (CR 701.23f), and a delayed trigger keeps its duration (CR 603.7c).</summary>
public sealed partial class InterpreterTests
{
    [Fact]
    [RuleRef("701.23f")]
    public void A_search_that_begins_writes_one_library_searched_and_its_answer_writes_none()
    {
        var table = new InterpreterTable();
        var (land, _) = Stocked(table);
        var begun = table.Resolve(table.OnStack(table.A, LandInstead()));

        // CR 701.23f: the search is the event a Searches trigger watches — once, for the library
        // searched, by the player searching it.
        var searched = Assert.Single(begun.OfType<LibrarySearched>());
        Assert.Equal(table.A, searched.Player);
        Assert.Equal(table.A, searched.Searcher);

        // CR 701.23h: the answer finishes the same search; it is not a second one.
        var answered = table.Answer(Option(table, land));
        Assert.DoesNotContain(answered, e => e is LibrarySearched);
    }

    [Fact]
    [RuleRef("603.7c")]
    public void A_delayed_trigger_keeps_the_duration_it_was_given()
    {
        var table = new InterpreterTable();
        table.Resolve(table.OnStack(table.A, new DelayedTrigger(new ManaSpent("k"), new GainLife(new Fixed(1)), Until: new UntilEndOfTurn())));

        // CR 603.7c: a delayed trigger with a stated duration is dropped when that duration ends,
        // so the state carries the duration for the cleanup to read.
        Assert.IsType<UntilEndOfTurn>(Assert.Single(table.State.DelayedTriggers).Until);
    }
}
