namespace MtgEngine.Rules.State;

/// <summary>Stable origin of an ability while its position in the effective ability list changes.</summary>
public sealed record AbilityOrigin(string Category, int Slot, int? EffectId = null,
    ObjectId? Source = null, string? Definition = null, int Face = 0, AbilityOrigin? Parent = null);
