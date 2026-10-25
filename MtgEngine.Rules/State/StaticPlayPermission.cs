namespace MtgEngine.Rules.State;

/// <summary>A static play permission belongs to one incarnation of its source and one ability slot.</summary>
public sealed record StaticPlayPermission(ObjectId Source, int AbilityIndex, AbilityOrigin? Origin = null);
