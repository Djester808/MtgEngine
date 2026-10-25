using System.Text.Json;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

/// <summary>
/// Owns the persisted search plan format and its private keys. It neither executes
/// moves nor advances replacement proposals; those stay on the shared resolution path.
/// </summary>
internal sealed class LibrarySearchContinuation(string path)
{
    private const string PlanPrefix = ":search-plan:";
    private string PlanKey => PlanPrefix + path;
    private string MovesKey => PlanKey + ":moves";
    private string BoardKey => PlanKey + ":board";
    public string MemoryPrefix => ":search:" + path + ":";

    public string? ReadBoard(ImmutableDictionary<string, string>? extra) => extra?.GetValueOrDefault(BoardKey);

    public ImmutableDictionary<string, string> SaveBoard(ImmutableDictionary<string, string> marks, string board) =>
        marks.SetItem(BoardKey, board);

    public string ParticipantMemory(int count, PlayerId player) =>
        MemoryPrefix + (count == 1 ? "" : player.Value + ":");

    public static string CardMemoryKey(string participant, LibrarySearchMemory memory) => participant + (memory switch
    {
        LibrarySearchMemory.Choice => "choice",
        LibrarySearchMemory.Selected => "selected",
        LibrarySearchMemory.Library => "library",
        LibrarySearchMemory.Arrived => "arrived",
        LibrarySearchMemory.Top => "top",
        LibrarySearchMemory.Bottom => "bottom",
        _ => throw new ArgumentOutOfRangeException(nameof(memory)),
    });

    public static string PositionedKey(string participant, ObjectId card) => participant + "positioned:" + card.Value;

    public static bool IsPrivateKey(string key) => key.StartsWith(PlanPrefix, StringComparison.Ordinal);

    public ImmutableList<LibrarySearchParticipant>? ReadParticipants(ImmutableDictionary<string, string>? extra) =>
        extra?.GetValueOrDefault(PlanKey) is { } saved
            ? JsonSerializer.Deserialize<ImmutableList<LibrarySearchParticipant>>(saved)
            : null;

    public ImmutableDictionary<string, ImmutableList<LibrarySearchMove>>? ReadMoves(ImmutableDictionary<string, string>? extra) =>
        extra?.GetValueOrDefault(MovesKey) is { } saved
            ? JsonSerializer.Deserialize<ImmutableDictionary<string, ImmutableList<LibrarySearchMove>>>(saved)
            : null;

    public ImmutableDictionary<string, string> SaveParticipants(ImmutableDictionary<string, string>? marks,
        ImmutableList<LibrarySearchParticipant> participants) =>
        (marks ?? ImmutableDictionary<string, string>.Empty).SetItem(PlanKey, JsonSerializer.Serialize(participants));

    public ImmutableDictionary<string, string> SaveMoves(ImmutableDictionary<string, string> marks,
        ImmutableDictionary<string, ImmutableList<LibrarySearchMove>> moves) =>
        marks.SetItem(MovesKey, JsonSerializer.Serialize(moves));

    public ImmutableDictionary<string, string>? Clear(ImmutableDictionary<string, string>? marks) =>
        marks?.Remove(PlanKey).Remove(MovesKey).Remove(BoardKey);
}

internal enum LibrarySearchMemory
{
    Choice,
    Selected,
    Library,
    Arrived,
    Top,
    Bottom,
}
