using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Faces;

/// <summary>
/// What a copy takes, CR 707.2: the copiable values are the values derived from the text
/// printed on the object, as modified by other copy effects and by its face-down status. Other
/// effects, status, counters and stickers are not copied. A definition and a face are all of
/// that the engine stores, because every other characteristic is computed by the layer system.
/// </summary>
[RuleRef("707.2")]
public sealed record CopiableValues(CardDefinition Definition, int FaceIndex, bool FaceDown);

/// <summary>
/// Copies of permanents and spells, CR 707, and the tokens that are copies of them. Pure
/// functions: reading the copiable values off an object, putting them onto another object, and
/// building the new object a copy token is.
/// </summary>
[RuleRef("707")]
public static class CopyRules
{
    /// <summary>The copiable values of a definition and one of its faces, CR 707.2.</summary>
    [RuleRef("707.2")]
    public static CopiableValues CopyOf(CardDefinition definition, int faceIndex, bool faceDown = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new CopiableValues(definition, faceIndex, faceDown);
    }

    /// <summary>
    /// The copiable values of an object as it is now, CR 707.2. The face that is up is part of
    /// them (CR 712.8e, 709.5b, 715.3c), and so is face-down status (CR 708.2); counters, damage,
    /// tapped and every other status are not, and are dropped here rather than by the caller.
    /// </summary>
    [RuleRef("707.2")]
    public static CopiableValues CopyOf(GameObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new CopiableValues(source.Definition, source.FaceIndex, source.FaceDown);
    }

    /// <summary>
    /// An object becomes a copy of something, CR 707.2: it takes the copiable values and keeps
    /// its own status, counters and damage, because those were never part of what was copied.
    /// </summary>
    [RuleRef("707.2")]
    public static GameObject Become(GameObject target, CopiableValues values)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(values);
        return target with
        {
            Definition = values.Definition,
            FaceIndex = values.FaceIndex,
            FaceDown = values.FaceDown,
        };
    }

    /// <summary>
    /// A new object that is a copy — the token an effect creates as a copy of a permanent
    /// (CR 707.2 and 111.4) or a copy of a spell put on the stack (CR 707.10). It has the
    /// copiable values and nothing else: no counters, no damage, untapped, phased in, no
    /// designations, and it is summoning sick like anything else that just arrived (CR 302.6).
    /// </summary>
    [RuleRef("707.2")]
    public static GameObject NewCopy(
        CopiableValues values,
        ObjectId id,
        PlayerId owner,
        PlayerId controller,
        ZoneKey zone,
        int timestamp,
        bool isToken = true)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new GameObject(id, values.Definition, values.FaceIndex, owner, controller, zone, timestamp)
        {
            FaceDown = values.FaceDown,
            IsToken = isToken,
        };
    }
}
