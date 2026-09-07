using MtgEngine.Cards;
using MtgEngine.Rules.State;
using Ev = MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Triggers;

/// <summary>
/// A triggered ability triggered and was put on the stack, CR 603.3. The stack object itself is
/// made by the <see cref="Ev.ObjectCreated"/> that precedes this; this event names which ability
/// of which source it is and what it triggered on, so the log says why the object is there and
/// the interpreter can bind "the triggering creature" without re-deriving it.
/// </summary>
[RuleRef("603.3")]
public sealed record TriggerFired(
    ObjectId StackObject,
    ObjectId? Source,
    int AbilityIndex,
    PlayerId Controller,
    ImmutableList<ObjectId> Subjects,
    PlayerId? SubjectPlayer,
    int? DelayedTrigger = null) : Ev.GameEvent
{
    /// <summary>
    /// A delayed trigger that fires once is gone, CR 603.7b. Everything else about the firing is
    /// already in the state: the stack object was created by the preceding event.
    /// </summary>
    [RuleRef("603.7b")]
    public override GameState Apply(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (DelayedTrigger is not { } id)
            return state;
        return state with { DelayedTriggers = state.DelayedTriggers.RemoveAll(d => d.Id == id) };
    }
}

/// <summary>What one firing of a trigger was about, CR 603.6: the objects, and the player if the event named one.</summary>
public sealed record TriggerSubject(ImmutableList<ObjectId> Objects, PlayerId? Player)
{
    /// <summary>
    /// Values the event itself named, which the firing carries onto the stack object so the
    /// resolution can read them: CR 106.12a's produced colour under
    /// <see cref="TriggerSystem.TappedForManaColor"/>, so "add an additional one mana of that
    /// color" has a colour to add. Mana is not an object (CR 106.1), so it cannot travel in
    /// <see cref="Objects"/>; a colour is a string, which is the channel
    /// <c>EffectContext.Chosen</c> already carries.
    /// </summary>
    [RuleRef("106.12a")]
    public ImmutableDictionary<string, string> Chosen { get; init; } = ImmutableDictionary<string, string>.Empty;

    public static TriggerSubject Nothing { get; } = new([], null);

    public static TriggerSubject Of(ObjectId subject) => new([subject], null);

    public static TriggerSubject Of(ObjectId subject, PlayerId player) => new([subject], player);

    public static TriggerSubject OfPlayer(PlayerId player) => new([], player);
}
