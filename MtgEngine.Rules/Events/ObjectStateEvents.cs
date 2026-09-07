using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Events;

// Events that change what an object is or what is in force, rather than where it is: a face
// turned over (CR 701.27, 710, 708), a continuous effect edited (615.9), a designation given or
// taken (702.180 and its kin), and a stack object retargeted (115.7).

/// <summary>
/// A permanent changed which face is up: transformed or converted (CR 701.27, 701.28), flipped
/// (CR 710), turned face up or face down (CR 708). The object keeps its identity, CR 712.8b.
/// </summary>
[RuleRef("701.27")]
public sealed record ObjectTurnedOver(ObjectId Object, int FaceIndex, TurnKind Kind) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        if (!state.Objects.TryGetValue(Object, out var obj))
            return state;
        var faceDown = Kind switch
        {
            TurnKind.TurnedFaceDown => true,
            TurnKind.TurnedFaceUp => false,
            _ => obj.FaceDown,
        };
        return state with { Objects = state.Objects.SetItem(Object, obj with { FaceIndex = FaceIndex, FaceDown = faceDown }) };
    }
}

public enum TurnKind
{
    Transform,
    Convert,
    Flip,
    TurnedFaceUp,
    TurnedFaceDown,
}

/// <summary>
/// A designation was given to or taken from an object: prepared (CR 702.180), suspected
/// (701.60), goaded-by (701.15a, the name carries the goader's seat), ring-bearer (701.54),
/// harnessed (701.64), monstrous (701.37). The names live in <c>Rules/Query/Designations.cs</c>.
/// </summary>
[RuleRef("702.180")]
public sealed record ObjectDesignated(ObjectId Object, string Designation, bool Given) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        if (!state.Objects.TryGetValue(Object, out var obj))
            return state;
        var designations = Given ? obj.Designations.Add(Designation) : obj.Designations.Remove(Designation);
        return state with { Objects = state.Objects.SetItem(Object, obj with { Designations = designations }) };
    }
}

/// <summary>A spell or ability on the stack had its targets changed, CR 115.7 (or new ones chosen, CR 114.6).</summary>
[RuleRef("115.7")]
public sealed record StackTargetsChanged(ObjectId StackObject, ImmutableList<ObjectId> Targets, ImmutableList<PlayerId> TargetPlayers) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        if (!state.Objects.TryGetValue(StackObject, out var obj) || obj.Stack is not { } entry)
            return state;
        return state with { Objects = state.Objects.SetItem(StackObject, obj with { Stack = entry with { Targets = Targets, TargetPlayers = TargetPlayers } }) };
    }
}
