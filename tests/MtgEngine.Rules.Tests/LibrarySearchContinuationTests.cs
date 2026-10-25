using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Library;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed class LibrarySearchContinuationTests
{
    private sealed class EmptyDefinitions : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => null;
        public CardDefinition? ByOracleId(string oracleId) => null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saved_choice_without_batch_plan_resumes_without_repeating_inspection(bool select)
    {
        var table = new InterpreterTable();
        var card = table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Search(ObjectFilter.Creature, Zone.Hand));
        var initial = table.State;
        var started = Subsystems.Wave2().Interpreter.Resolve(initial, spell);
        // Emulate the pre-batch choice schema, which had no search-plan keys.
        var events = started.Events.Select(e => e is ChoiceRequested requested
            ? (GameEvent)(requested with
            {
                Choice = requested.Choice with
                {
                    Context = requested.Choice.Context with
                    {
                        Extra = requested.Choice.Context.Extra.RemoveRange(
                        requested.Choice.Context.Extra.Keys.Where(LibrarySearchContinuation.IsPrivateKey)),
                    },
                }
            }) : e).ToImmutableList();
        var restored = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        var pending = Assert.IsType<PendingChoice>(restored.Pending);
        var answer = new AnswerChoice(table.A, pending.Id,
            select ? [pending.Options.Single(option => option.Object == card).Id] : []);
        var outcome = Assert.IsType<Applied>(new Game(Subsystems.Wave2(), new EmptyDefinitions()).Apply(restored, answer));
        Assert.DoesNotContain(outcome.Events, e => e is CardsLookedAt or LibrarySearched);
        Assert.Single(outcome.Events.OfType<ZoneReordered>(), e => e.Shuffle);
        var final = GameReducer.Apply(restored, EventLogSerializer.Read(EventLogSerializer.Write(outcome.Events)));
        Assert.Null(final.Pending);
        Assert.Empty(final.LibraryInspections);
        Assert.Equal(select ? 1 : 0, final.In(ZoneKey.Of(Zone.Hand, table.A)).Count);
        Assert.Equal(select ? 1 : 2, final.In(ZoneKey.Of(Zone.Library, table.A)).Count);
        Assert.IsType<Refused>(new Game(Subsystems.Wave2(), new EmptyDefinitions()).Apply(final, answer));
    }

    [Fact]
    public void Reads_prior_persisted_plan_format_and_clears_only_this_search()
    {
        // Literal prior-format payloads: generating these with the new writer would
        // not test compatibility with choices already stored before extraction.
        var extra = ImmutableDictionary<string, string>.Empty
            .Add(":search-plan:0.1", """
                [{"Searcher":{"Value":2},"LibraryOwner":{"Value":1},"Plan":{"Zones":[0],"Inspected":[{"Value":7}],"Candidates":[{"Value":7}],"Minimum":0,"Maximum":1}}]
                """)
            .Add(":search-plan:0.1:moves", """
                {":search:0.1:":[{"Card":{"Value":7},"To":1,"Where":0,"Tapped":false,"WithinLibrary":false}]}
                """)
            .Add(":search-plan:0.2", "another search")
            .Add("unrelated", "keep");
        var continuation = new LibrarySearchContinuation("0.1");
        var participant = Assert.Single(continuation.ReadParticipants(extra)!);
        Assert.Equal(new PlayerId(2), participant.Searcher);
        Assert.Equal(new PlayerId(1), participant.LibraryOwner);
        Assert.Equal(new ObjectId(7), Assert.Single(participant.Plan.Candidates));
        var memory = continuation.ParticipantMemory(1, participant.Searcher);
        Assert.Equal(":search:0.1:", memory);
        Assert.Equal(":search:0.1:2:", continuation.ParticipantMemory(4, participant.Searcher));
        var move = Assert.Single(continuation.ReadMoves(extra)![memory]);
        Assert.Equal(new LibrarySearchMove(new ObjectId(7), Zone.Hand, Placement.Default, false, false), move);
        var retained = continuation.Clear(extra)!;
        Assert.Equal(2, retained.Count);
        Assert.Equal("another search", retained[":search-plan:0.2"]);
        Assert.Equal("keep", retained["unrelated"]);
    }
}
