using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

/// <summary>Shared resolution operations needed by search; no search sequencing lives here.</summary>
internal interface ILibrarySearchRuntime
{
    GameState State { get; }
    ImmutableList<ObjectId>? Cards(LibrarySearchScope participant, LibrarySearchMemory memory);
    void RememberCards(LibrarySearchScope participant, LibrarySearchMemory memory, IEnumerable<ObjectId> cards);
    bool Positioned(LibrarySearchScope participant, ObjectId card);
    void MarkPositioned(LibrarySearchScope participant, ObjectId card);
    LibrarySearchPlan Plan(Search search, PlayerId searcher, PlayerId owner);
    ImmutableList<LibrarySearchMove> PlanMoves(Search search, ImmutableList<ObjectId> found);
    ImmutableList<LibrarySearchParticipant>? SavedParticipants();
    void SaveParticipants(ImmutableList<LibrarySearchParticipant> participants);
    ImmutableDictionary<PlayerId, ImmutableList<LibrarySearchMove>>? SavedMoves(ImmutableList<LibrarySearchParticipant> participants);
    void SaveMoves(ImmutableDictionary<PlayerId, ImmutableList<LibrarySearchMove>> moves);
    string? SavedBoard();
    void SaveBoard(string board);
    void ClearContinuation();
    bool HasLegacySelection();
    bool Halted { get; }
    PlayerId? AnswerPlayer { get; }
    string? AnswerKind { get; }
    ImmutableList<PlayerId> Searchers(Search search);
    PlayerId LibraryOwner(Search search, PlayerId searcher);
    ImmutableList<ObjectId>? Answer();
    void Ask(PlayerId player, LibrarySearchPlan plan);
    void Inspect(LibrarySearchParticipant participant);
    void RememberResult(string? key, IEnumerable<ObjectId> cards);
    void Reveal(ImmutableList<ObjectId> cards, PlayerId searcher);
    ObjectId? Move(LibrarySearchMove move, string board);
    void Shuffle(PlayerId owner, PlayerId searcher);
    void Order(ZoneKey zone, ImmutableList<ObjectId> order, PlayerId searcher);
    void EndInspection(PlayerId searcher);
}

internal readonly record struct LibrarySearchScope(int Count, PlayerId Searcher);
