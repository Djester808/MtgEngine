using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Replacement;

/// <summary>
/// Which proposed event each <see cref="ReplacedEvent"/> watches for, CR 614.1. One arm per
/// record in the vocabulary; a record with no arm matches nothing, which is the honest answer
/// until the event it needs exists.
/// </summary>
public sealed partial class ReplacementSystem
{
    [RuleRef("614.1")]
    private bool Matches(GameState state, ReplacedEvent instead, GameEvent proposed, EffectContext context) => instead switch
    {
        WouldEnterBattlefield w => proposed is ObjectMoved m
            && m.To.Zone.Zone == Zone.Battlefield
            && _query.Matches(state, w.What, m.To.Id, context)
            && (w.UnderControlOf is null || Names(state, w.UnderControlOf, context, ControllerOf(state, m.To))),

        WouldLeaveBattlefield w => Leaves(state, proposed, Zone.Battlefield, out var left)
            && _query.Matches(state, w.What, left, context),

        // CR 700.4: a permanent dies when it is put into a graveyard from the battlefield.
        WouldDie w => proposed is ObjectMoved dead
            && dead.To.Zone.Zone == Zone.Graveyard
            && WasIn(state, dead.From, Zone.Battlefield)
            && _query.Matches(state, w.What, dead.From, context),

        WouldBePutIntoZone w => proposed is ObjectMoved put
            && put.To.Zone.Zone == w.To
            && (w.From is null || WasIn(state, put.From, w.From.Value))
            && _query.Matches(state, w.What, Subject(state, put), context),

        WouldBeExiled w => proposed is ObjectMoved gone
            && gone.To.Zone.Zone == Zone.Exile
            && _query.Matches(state, w.What, Subject(state, gone), context),

        WouldDiscard w => proposed is ObjectMoved pitched
            && pitched.To.Zone.Zone == Zone.Graveyard
            && WasIn(state, pitched.From, Zone.Hand)
            && Names(state, w.Who, context, pitched.To.Owner)
            && (w.What is null || _query.Matches(state, w.What, Subject(state, pitched), context)),

        // CR 615: prevention and replacement of damage read the same event.
        WouldBeDealtDamage w => proposed is DamageDealt hit
            && hit.ToObject is { } target
            && _query.Matches(state, w.To, target, context)
            && Combat(w.Combat, hit)
            && (w.By is null || _query.Matches(state, w.By, hit.Source, context)),

        PlayerWouldBeDealtDamage w => proposed is DamageDealt struck
            && struck.ToPlayer is { } who
            && Names(state, w.Who, context, who)
            && Combat(w.Combat, struck)
            && (w.By is null || _query.Matches(state, w.By, struck.Source, context)),

        WouldDealDamage w => proposed is DamageDealt dealt
            && Combat(w.Combat, dealt)
            && _query.Matches(state, w.Source, dealt.Source, context)
            && Recipient(state, w, dealt, context),

        // CR 121.1: "except the first one they draw in each of their draw steps" is a narrowing of
        // which draws this replaces, not of what it does to them.
        WouldDraw w => DrawingPlayer(proposed) is { } drawing
            && Names(state, w.Who, context, drawing)
            && (w.FirstEachTurn is not true || state.Player(drawing).ObjectsDrawnThisTurn == 0)
            && (!w.ExceptFirstInDrawStep || !TheirDrawStepsFirstDraw(state, drawing)),

        WouldGainLife w => proposed is LifeChanged gained
            && gained.Delta > 0
            && Names(state, w.Who, context, gained.Player),

        WouldLoseLife w => proposed is LifeChanged lost
            && lost.Delta < 0
            && Names(state, w.Who, context, lost.Player),

        WouldPutCounters w => proposed is CountersChanged counters
            && counters.Delta > 0
            && (w.Counter is null || string.Equals(w.Counter, counters.Kind, StringComparison.OrdinalIgnoreCase))
            && _query.Matches(state, w.On, counters.Id, context),

        WouldCreateTokens w => proposed is ObjectCreated created
            && created.Object.IsToken
            && Names(state, w.Who, context, created.Object.InitialController)
            && (w.What is null || _query.Matches(state, w.What, created.Object.Id, context)),

        WouldBeCountered w => Verb(proposed, "countered", out var countered)
            && _query.Matches(state, w.Spell, countered, context),

        WouldBeDestroyed w => Verb(proposed, "destroyed", out var destroyed)
            && _query.Matches(state, w.What, destroyed, context),

        WouldBeSacrificed w => Verb(proposed, "sacrificed", out var sacrificed)
            && _query.Matches(state, w.What, sacrificed, context),

        WouldUntap w => proposed is ObjectChanged { Tapped: false } untapped
            && (!w.DuringUntapStepOnly || state.Turn.Step == Step.Untap)
            && _query.Matches(state, w.What, untapped.Id, context),

        WouldTap w => proposed is ObjectChanged { Tapped: true } tapped
            && _query.Matches(state, w.What, tapped.Id, context),

        WouldBeTapped w => proposed is ObjectChanged { Tapped: true } marked
            && _query.Matches(state, w.What, marked.Id, context),

        WouldBeTurnedFaceUp w => proposed is ObjectChanged { FaceDown: false } up
            && _query.Matches(state, w.What, up.Id, context),

        // CR 605.1a with 106.12: the rider narrows this to mana a mana ability added for a
        // permanent that was tapped to pay for it.
        WouldAddMana w => proposed is ManaChanged mana
            && Names(state, w.Who, context, mana.Player)
            && (!w.TappedFor || TappedForMana(state, mana)),

        WouldLoseGame w => proposed is PlayerLost defeat
            && Names(state, w.Who, context, defeat.Player),

        // CR 701.24: a shuffle, and not cards put back into a zone in a chosen order, which is
        // the same event with the flag clear.
        WouldShuffle w => proposed is ZoneReordered { Shuffle: true } shuffled
            && shuffled.Zone.Player is { } whose
            && Names(state, w.Who, context, whose),

        WouldBeginStep w => proposed is StepBegan step
            && step.Step == w.Step
            && Names(state, w.Whose, context, state.Turn.ActivePlayer),

        _ => false,
    };

    /// <summary>Whether the effect's player reference names this player right now.</summary>
    private bool Names(GameState state, PlayerRef who, EffectContext context, PlayerId player) =>
        _query.Players(state, who, context).Contains(player);

    private static bool Combat(bool? wanted, DamageDealt damage) => wanted is null || wanted.Value == damage.Combat;

    private static bool WasIn(GameState state, ObjectId id, Zone zone) =>
        state.Objects.TryGetValue(id, out var obj) && obj.Zone.Zone == zone;

    /// <summary>The object a move is about: the one that was there, or the one arriving if it was not.</summary>
    private static ObjectId Subject(GameState state, ObjectMoved moved) =>
        state.Objects.ContainsKey(moved.From) ? moved.From : moved.To.Id;

    private static bool Leaves(GameState state, GameEvent proposed, Zone zone, out ObjectId what)
    {
        what = default;
        if (proposed is not ObjectMoved moved || moved.To.Zone.Zone == zone || !WasIn(state, moved.From, zone))
            return false;
        what = moved.From;
        return true;
    }

    private static bool Verb(GameEvent proposed, string verb, out ObjectId what)
    {
        what = default;
        if (proposed is not ObjectLeftBy left || !string.Equals(left.Verb, verb, StringComparison.OrdinalIgnoreCase))
            return false;
        what = left.Object;
        return true;
    }

    /// <summary>
    /// CR 615.1: a prevention shield covers this damage when the damage is to something the
    /// effect applies to, from a source it names, in the combat it names.
    /// </summary>
    [RuleRef("615.1")]
    private bool Shields(GameState state, ActiveEffect active, PreventDamage shield, DamageDealt damage)
    {
        if (!Combat(shield.Combat, damage))
            return false;

        var context = Context(active.Source, active.Controller, damage);
        if (shield.From is { } from && !_query.Matches(state, from, damage.Source, context))
            return false;

        if (damage.ToObject is { } hit)
            return active.AppliesTo.Contains(hit);

        return damage.ToPlayer is { } who
            && shield.ToPlayersToo
            && active.AppliesToPlayers.Contains(who);
    }
}
