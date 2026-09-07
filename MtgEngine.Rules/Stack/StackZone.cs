using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Stack;

/// <summary>
/// The stack, CR 405. One shared zone, held bottom-first, so the last entry is the top and
/// the next object to resolve, CR 405.2. Putting something on the stack is the only way a
/// spell or an activated ability comes into existence, CR 601.2a and 602.2a.
/// </summary>
[Cards.RuleRef("405")]
public static class StackZone
{
    public static ZoneKey Key { get; } = ZoneKey.Shared(CardZone.Stack);

    public static bool IsEmpty(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.In(Key).Count == 0;
    }

    /// <summary>The object that would resolve next, CR 405.2: the last of the bottom-first list.</summary>
    public static ObjectId? Top(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var stack = state.In(Key);
        return stack.Count > 0 ? stack[^1] : null;
    }

    /// <summary>
    /// CR 601.2a: the card moves from its zone to the stack and becomes a spell, a new object
    /// carrying the choices made while casting it. <paramref name="faceIndex"/> is the face the
    /// player chose (CR 601.3b): only that face is put on the stack and only its characteristics
    /// are evaluated there, CR 712.11c, 709.3a and 715.3a.
    /// </summary>
    [Cards.RuleRef("601.2a")]
    public static ObjectMoved MoveSpellToStack(GameState state, GameObject card, StackEntry entry, int? faceIndex = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(card);
        var spell = card with
        {
            Id = new ObjectId(state.NextObjectId),
            FaceIndex = faceIndex ?? card.FaceIndex,
            Zone = Key,
            Timestamp = state.NextTimestamp,
            Tapped = false,
            Damage = 0,
            AttachedTo = null,
            Stack = entry,
        };
        return new ObjectMoved(card.Id, spell, -1);
    }

    /// <summary>
    /// CR 602.2a: the activated ability goes on the stack as an object that is not a card and
    /// exists only there. It keeps its source's definition so its text can be read while it
    /// resolves, and <see cref="StackEntry.AbilityIndex"/> says which ability it is.
    /// </summary>
    [Cards.RuleRef("602.2a")]
    public static ObjectCreated CreateAbilityOnStack(GameState state, GameObject source, PlayerId controller, StackEntry entry)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(source);
        var ability = new GameObject(
            new ObjectId(state.NextObjectId),
            source.Definition,
            source.FaceIndex,
            source.Owner,
            controller,
            Key,
            state.NextTimestamp)
        {
            Stack = entry,
            SummoningSick = false,
        };
        return new ObjectCreated(ability, -1);
    }

    /// <summary>Whether a stack object is an ability rather than a spell, CR 113.7a.</summary>
    public static bool IsAbility(GameObject stackObject)
    {
        ArgumentNullException.ThrowIfNull(stackObject);
        return stackObject.Stack?.AbilityIndex is not null;
    }
}
