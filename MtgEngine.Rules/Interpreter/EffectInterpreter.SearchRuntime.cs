using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Library;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

public sealed partial class EffectInterpreter
{
    /// <summary>Adapts search to the existing prompt, movement and replacement machinery.</summary>
    private sealed class SearchRuntime(EffectInterpreter interpreter, IZoneSystem zones, EffectRun initial) : ILibrarySearchRuntime
    {
        private EffectRun run = initial;
        public EffectRun Result => run;
        public GameState State => run.State;
        public ImmutableList<ObjectId>? Cards(LibrarySearchScope participant, LibrarySearchMemory memory) =>
            run.Context.Remembered.GetValueOrDefault(LibrarySearchContinuation.CardMemoryKey(continuation.ParticipantMemory(participant.Count, participant.Searcher), memory));
        public void RememberCards(LibrarySearchScope participant, LibrarySearchMemory memory, IEnumerable<ObjectId> cards) =>
            run = Recall(run, LibrarySearchContinuation.CardMemoryKey(continuation.ParticipantMemory(participant.Count, participant.Searcher), memory), cards);
        public bool Positioned(LibrarySearchScope participant, ObjectId card) =>
            run.Context.Remembered.ContainsKey(LibrarySearchContinuation.PositionedKey(continuation.ParticipantMemory(participant.Count, participant.Searcher), card));
        public void MarkPositioned(LibrarySearchScope participant, ObjectId card) =>
            run = Recall(run, LibrarySearchContinuation.PositionedKey(continuation.ParticipantMemory(participant.Count, participant.Searcher), card), []);
        public LibrarySearchPlan Plan(Search search, PlayerId searcher, PlayerId owner) =>
            interpreter.searchPlanner.Plan(run.State, search, run.Context, searcher, owner);
        public ImmutableList<LibrarySearchMove> PlanMoves(Search search, ImmutableList<ObjectId> found) =>
            interpreter.searchPlanner.Moves(run.State, search, run.Context, found);
        private readonly LibrarySearchContinuation continuation = new(initial.Path);
        public ImmutableList<LibrarySearchParticipant>? SavedParticipants() => continuation.ReadParticipants(run.Answer?.Context.Extra);
        public void SaveParticipants(ImmutableList<LibrarySearchParticipant> participants) =>
            run = run with { Marks = continuation.SaveParticipants(run.Marks, participants) };
        public ImmutableDictionary<PlayerId, ImmutableList<LibrarySearchMove>>? SavedMoves(ImmutableList<LibrarySearchParticipant> participants)
        {
            var saved = continuation.ReadMoves(run.Answer?.Context.Extra);
            return saved is null ? null : participants.ToImmutableDictionary(participant => participant.Searcher,
                participant => saved[continuation.ParticipantMemory(participants.Count, participant.Searcher)]);
        }
        public void SaveMoves(ImmutableDictionary<PlayerId, ImmutableList<LibrarySearchMove>> moves) =>
            run = run with
            {
                Marks = continuation.SaveMoves(run.Marks!, moves.ToImmutableDictionary(
                    pair => continuation.ParticipantMemory(moves.Count, pair.Key), pair => pair.Value)),
            };
        public string? SavedBoard() => continuation.ReadBoard(run.Answer?.Context.Extra);
        public void SaveBoard(string board) => run = run with { Marks = continuation.SaveBoard(run.Marks!, board) };
        public void ClearContinuation()
        {
            var keys = run.Context.Remembered.Keys.Where(key => key.StartsWith(continuation.MemoryPrefix, StringComparison.Ordinal));
            run = run with
            {
                Marks = continuation.Clear(run.Marks),
                Context = run.Context with { Remembered = run.Context.Remembered.RemoveRange(keys) },
            };
        }
        public bool HasLegacySelection() => run.Context.Remembered.ContainsKey(continuation.MemoryPrefix + "selected");
        public bool Halted => run.Halted;
        public PlayerId? AnswerPlayer => run.Answer?.Player;
        public string? AnswerKind => run.Answer?.Kind;
        public ImmutableList<PlayerId> Searchers(Search search) => Searchers(run, search);
        public PlayerId LibraryOwner(Search search, PlayerId searcher) => LibraryOwner(run, search, searcher);
        public ImmutableList<ObjectId>? Answer() => Answer(run);
        public void Ask(PlayerId player, LibrarySearchPlan plan) => run = Ask(run, player, plan);
        public void Inspect(LibrarySearchParticipant participant) => run = Inspect(run, participant);
        public void RememberResult(string? key, IEnumerable<ObjectId> cards) => run = Remember(run, key, cards);
        public void Reveal(ImmutableList<ObjectId> cards, PlayerId searcher) => run = Reveal(run, cards, searcher);
        public ObjectId? Move(LibrarySearchMove move, string board)
        {
            (run, var landed) = Move(run, move, board);
            return landed;
        }
        public void Shuffle(PlayerId owner, PlayerId searcher) => run = Shuffle(run, owner, searcher);
        public void Order(ZoneKey zone, ImmutableList<ObjectId> order, PlayerId searcher) => run = Order(run, zone, order, searcher);
        public void EndInspection(PlayerId searcher) => run = EndInspection(run, searcher);

        private ImmutableList<PlayerId> Searchers(EffectRun run, Search search) =>
            InTurnOrder(run.State, interpreter.PlayersOrYou(run, search.Who));

        private PlayerId LibraryOwner(EffectRun run, Search search, PlayerId searcher) =>
            search.LibraryOf is null ? searcher : interpreter.FirstPlayer(run, search.LibraryOf);

        private ImmutableList<ObjectId>? Answer(EffectRun run) =>
            Answered(run) is { } answer ? Named(run.State, answer) : null;

        private EffectRun Ask(EffectRun run, PlayerId player, LibrarySearchPlan plan) =>
            interpreter.Ask(run, player, ChoiceKinds.Search, "Search for a card.",
                OptionsFor(run.State, plan.Candidates), plan.Minimum, plan.Maximum);

        private EffectRun Inspect(EffectRun run, LibrarySearchParticipant participant)
        {
            if (participant.Plan.Zones.Contains(Zone.Library))
                run = Emit(run, new LibrarySearched(participant.LibraryOwner, participant.Searcher));
            if (participant.Plan.Inspected.IsEmpty)
                return run;
            run = Emit(run, new CardsLookedAt([participant.Searcher], DisclosedCards(run, participant.Plan.Inspected)));
            var cards = participant.Plan.Inspected.Where(id =>
                run.State.Objects[id].Zone == ZoneKey.Of(Zone.Library, participant.LibraryOwner)).ToImmutableList();
            return InspectLibrary(run, "search", participant.Searcher, participant.LibraryOwner, cards);
        }

        private EffectRun Remember(EffectRun run, string? key, IEnumerable<ObjectId> cards) => Recall(run, key, cards);

        private EffectRun Reveal(EffectRun run, ImmutableList<ObjectId> cards, PlayerId searcher) =>
            RevealCards(run, cards, searcher);

        private (EffectRun Run, ObjectId? Landed) Move(EffectRun run, LibrarySearchMove move, string board) =>
            interpreter.Relocate(run, move.Card, move.To, move.Where, move.Tapped, controller: null, batchBoard: board);

        private EffectRun Shuffle(EffectRun run, PlayerId owner, PlayerId searcher)
        {
            var count = run.Events.Count;
            run = interpreter.ProposeThrough(run, current => zones.Shuffle(current.State, owner, current.Context.UsedReplacements));
            var reordered = run.Events.Skip(count).OfType<ZoneReordered>()
                .LastOrDefault(e => e.Zone == ZoneKey.Of(Zone.Library, owner));
            return reordered is null ? run : RefreshSearchIdentities(run, reordered, searcher);
        }

        private EffectRun Order(EffectRun run, ZoneKey zone, ImmutableList<ObjectId> order, PlayerId searcher)
        {
            var reordered = new ZoneReordered(zone, order);
            return RefreshSearchIdentities(Emit(run, reordered), reordered, searcher);
        }

        private static EffectRun RefreshSearchIdentities(EffectRun run, ZoneReordered reordered, PlayerId searcher)
        {
            var continuation = new LibrarySearchContinuation(run.Path);
            var viewers = continuation.ReadParticipants(run.Marks)?.Select(participant => participant.Searcher) ?? [searcher];
            return RefreshLibraryIdentities(run, continuation.MemoryPrefix, reordered, "search", viewers);
        }

        private EffectRun EndInspection(EffectRun run, PlayerId searcher) => EndLibraryInspection(run, "search", searcher);
    }
}
