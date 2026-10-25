namespace MtgEngine.Domain.Models;

/// <summary>An immutable batch appended to a game's initial event log.</summary>
public sealed class PersistedGameEvent
{
    public Guid GameId { get; set; }
    public int Sequence { get; set; }
    public string Json { get; set; } = "[]";
}
