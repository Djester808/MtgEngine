using MtgEngine.Cards;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Events;

// Events about damage that was not dealt. Damage that happens is DamageDealt in GameEvent.cs; the
// record below is the other half, the one the log had no way to say — a prevention effect being
// applied (CR 615.1) is the one happening whose whole visible result is that another event got
// smaller or never happened at all.

/// <summary>
/// A prevention effect was applied to damage that would have been dealt, CR 615.13: some or all of
/// that damage was prevented and so is never dealt (CR 615.6). One of these is written per damage
/// event a single application of the effect touched, which is what "each time a prevention effect
/// is applied to one or more simultaneous damage events" asks a trigger to count.
/// <para>
/// The log needs it because a prevention is invisible in the events it leaves behind: the surviving
/// <see cref="DamageDealt"/> carries the reduced amount and says nothing about the shield, and a
/// prevention that prevents all of the damage leaves no event at all. Neither of the two facts
/// CR 615.13 triggers on — that a prevention happened, and how much it prevented — can be recovered
/// from the rest of the log, so they are recorded here or nowhere.
/// </para>
/// <para>
/// <paramref name="Source"/> is the object that would have dealt the damage (CR 615.1), and
/// <paramref name="ToObject"/>/<paramref name="ToPlayer"/> the recipient it would have been dealt
/// to — exactly one of the two, matching <see cref="DamageDealt"/>'s shape, so a trigger that
/// narrows the recipient has the same thing to ask about whether the damage landed or not.
/// <paramref name="Prevented"/> is what this application actually removed from the event, not what
/// the event was going to deal: a shield of 2 over 5 damage prevented 2 (CR 615.7). It is never
/// zero on a written event — CR 615.12's unpreventable damage still has the effect applied to it
/// and still prevents nothing, and a trigger on that would fire on damage nothing saved anyone
/// from, so the emitter writes no event rather than a zero. <paramref name="PreventedBy"/> is the
/// object whose effect was applied, which is what a trigger's "this way" names; it is null for a
/// prevention no object is behind, and such a prevention can never match a "this way".
/// </para>
/// </summary>
[RuleRef("615.13")]
public sealed record DamagePrevented(
    ObjectId Source,
    ObjectId? ToObject,
    PlayerId? ToPlayer,
    int Prevented,
    ObjectId? PreventedBy) : GameEvent, IDamagePrevented
{
    /// <summary>
    /// CR 615: a prevention changes nothing beyond the damage not being dealt, and the damage not
    /// being dealt is already the whole of what the surviving <see cref="DamageDealt"/> says (or
    /// does not say). Folding this event a second time would take the damage off twice, so it folds
    /// to the state it was given — the event exists to be read, by a trigger and by a replay, not to
    /// move anything. It follows that a log with the prevention in it replays to the same state as
    /// a log without it, which is what lets the emitter be added to an engine whose stored games
    /// predate it.
    /// </summary>
    public override GameState Apply(GameState state) => state;
}
