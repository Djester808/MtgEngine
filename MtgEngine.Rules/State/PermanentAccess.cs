using MtgEngine.Cards;
using MtgEngine.Rules.Engine;

namespace MtgEngine.Rules.State;

/// <summary>
/// Validated server-side access to existing battlefield objects, including phased-out
/// permanents. This is not targeting permission or a viewer-safe projection. Stored
/// status remains on GameObject; current characteristics belong to ICharacteristics.
/// </summary>
[RuleRef("110.1")]
public static class PermanentAccess
{
    /// <summary>
    /// Returns the existing object only when its identity, zone and authoritative
    /// membership agree. Tokens and legacy cards without physical-card IDs are valid.
    /// </summary>
    public static GameObject? Find(GameState state, ObjectId id)
    {
        ArgumentNullException.ThrowIfNull(state);
        var battlefield = ZoneKey.Shared(Zone.Battlefield);
        return state.Objects.TryGetValue(id, out var obj)
            && obj.Id == id && obj.Zone == battlefield && state.In(battlefield).Contains(id)
            ? obj : null;
    }

    public static GameObject Require(GameState state, ObjectId id) =>
        Find(state, id) ?? throw new InvalidOperationException("The object is not a current permanent.");

    /// <summary>
    /// Evaluates the same immutable snapshot after membership validation. Ownership,
    /// printing and status are not replaced by evaluated controller/type/strength.
    /// </summary>
    public static Engine.Characteristics Evaluate(GameState state, ObjectId id, ICharacteristics characteristics)
    {
        ArgumentNullException.ThrowIfNull(characteristics);
        Require(state, id);
        return characteristics.Of(state, id);
    }
}
