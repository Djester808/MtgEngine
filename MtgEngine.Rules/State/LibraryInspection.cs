namespace MtgEngine.Rules.State;

/// <summary>Active library access, separate from historical card snapshots and choice eligibility.</summary>
public sealed record LibraryInspection(PlayerId Viewer, PlayerId Owner, ImmutableList<ObjectId> Cards)
{
    public string? Resolution { get; init; }
    /// <summary>Explicit public reveal, rather than access granted by looking or searching.</summary>
    public bool PublicReveal { get; init; }
}
