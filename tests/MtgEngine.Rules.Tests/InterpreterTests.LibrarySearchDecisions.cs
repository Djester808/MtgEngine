using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed partial class InterpreterTests
{
    [Fact]
    public void Search_plans_all_destinations_before_moving_and_retains_only_results_for_the_next_prompt()
    {
        var table = new InterpreterTable(new LayerSystem());
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        var alreadyInHand = ObjectFilter.Any with
        {
            Also = new SharesNameWith(new Each(ObjectFilter.Any with { In = Zone.Hand, OwnedBy = new You() })),
        };
        table.Resolve(table.OnStack(table.A, new Sequence([
            new Search(ObjectFilter.Any, Zone.Hand, UpTo: new Fixed(2), RememberAs: "found",
                Instead: [new SearchDestination(alreadyInHand, Zone.Graveyard)]),
            new May(new GainLife(new Fixed(1))), new Exile(new Remembered("found")),
        ])));
        var events = table.Answer([.. table.Pending.Options.Select(option => option.Id)]);
        Assert.Equal(2, table.In(Zone.Hand, table.A).Count);
        Assert.Empty(table.In(Zone.Graveyard, table.A));
        Assert.Equal("may", table.Pending.Kind);
        Assert.DoesNotContain(table.Pending.Context.Extra.Keys, key =>
            key.StartsWith(":search-plan:", StringComparison.Ordinal) || key.Contains(":search:", StringComparison.Ordinal));
        Assert.Single(events.OfType<ZoneReordered>(), e => e.Shuffle);
        var restored = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write([new ChoiceRequested(table.Pending)])));
        var pending = restored.Pending!;
        var completed = table.Subject.Resume(new ChoiceAnswered(pending.Id, pending.Player, ["no"]).Apply(restored),
            new Resumption(pending.Kind, pending.Player, [pending.Options.Single(option => option.Id == "no")], pending.Context));
        Assert.Empty(completed.State.In(ZoneKey.Of(Zone.Hand, table.A)));
        Assert.Equal(2, completed.State.In(ZoneKey.Shared(Zone.Exile)).Count);
        Assert.DoesNotContain(completed.Events, e => e is LibrarySearched or ZoneReordered);
    }
}
