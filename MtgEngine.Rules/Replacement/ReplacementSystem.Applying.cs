using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Replacement;

/// <summary>
/// What applying a replacement does to the event, CR 614.1. The five verbs that modify an
/// event modify it; anything else is the "instead" of CR 614.1a — the event does not happen
/// and the effect runs in its place.
/// </summary>
public sealed partial class ReplacementSystem
{
    private Applied Apply(GameState state, GameEvent current, Candidate candidate) =>
        candidate.Active is { Effect: PreventDamage shield } active
            ? Spend(state, current, active, shield, candidate)
            : Perform(state, current, candidate.Ability!.With, candidate);

    /// <summary>
    /// CR 615.7: a shield for a specific amount is spent by what it prevents — each 1 damage
    /// prevented reduces the remaining shield by 1, and once it reaches 0 the rest of the damage
    /// is dealt normally. The remainder is written back as a smaller shield through
    /// <see cref="ActiveEffectChanged"/>, so the spending is in the log and survives a replay;
    /// a shield with nothing left ends (a null effect). "Prevent all damage" carries no amount
    /// and is never used up (CR 615.9).
    /// </summary>
    [RuleRef("615.7")]
    private Applied Spend(GameState state, GameEvent current, ActiveEffect active, PreventDamage shield, Candidate candidate)
    {
        if (current is not DamageDealt damage)
            return new Applied(current, state, []);

        if (shield.Amount is null)
            return new Applied(Prevent(state, current, null, candidate), state, []);

        var available = Math.Max(0, _query.Evaluate(state, shield.Amount, Context(candidate.Source, candidate.Controller, current)));
        var prevented = Math.Min(available, Math.Max(0, damage.Amount));
        var left = damage.Amount - prevented;
        var remaining = available - prevented;

        var change = new ActiveEffectChanged(active.Id, remaining == 0 ? null : shield with { Amount = new Fixed(remaining) });
        return new Applied(left == 0 ? null : damage with { Amount = left }, change.Apply(state), [change]);
    }

    private Applied Perform(GameState state, GameEvent current, Effect effect, Candidate candidate)
    {
        switch (effect)
        {
            case EntersTapped:
                return new Applied(Entering(current, obj => obj with { Tapped = true }), state, []);

            // CR 702.180 with 614.1c: "enters prepared" puts the designation on the object as it
            // enters; the selector is the entering object itself on this road.
            case Cards.Prepare:
                return new Applied(Entering(current, obj => obj with { Designations = obj.Designations.Add(Query.Designations.Prepared) }), state, []);

            case EntersWithCounters counters:
                {
                    var n = _query.Evaluate(state, counters.N, Context(candidate.Source, candidate.Controller, current));
                    return new Applied(Entering(current, obj => WithCounters(obj, counters.Counter, n)), state, []);
                }

            case EntersAs how:
                return new Applied(Entering(current, obj => EnteringAs(state, obj, how, candidate, current)), state, []);

            case PreventThatDamage prevention:
                return new Applied(Prevent(state, current, prevention.N, candidate), state, []);

            case ModifyAmount modify:
                return new Applied(Modify(current, modify), state, []);

            // CR 614.1c with 608.2d: "as this enters, choose …". The choosing is part of the
            // entering, not a replacement of it, so the event still happens; reading it as
            // CR 614.1a's "instead" drops the move and the permanent never arrives at all.
            case ChooseValue or Sequence when Arriving(current) && Chooses(effect):
                return Choosing(state, effect, candidate, current);

            case Sequence sequence:
                {
                    var applied = new Applied(current, state, []);
                    foreach (var step in sequence.Steps)
                    {
                        if (applied.Event is null)
                            return applied;
                        var next = Perform(applied.State, applied.Event, step, candidate);
                        applied = new Applied(next.Event, next.State, applied.Extra.AddRange(next.Extra));
                    }

                    return applied;
                }

            // CR 614.1a: the event does not happen, and what the effect says happens instead.
            case ReplaceWith replacement:
                return Instead(state, replacement.Do, candidate, current);

            default:
                return Instead(state, effect, candidate, current);
        }
    }

    /// <summary>
    /// CR 614.1a: the event does not happen and the effect runs in its place. An effect that has
    /// a choice of its own — riot's "an additional +1/+1 counter, or haste" is a
    /// <see cref="ChooseMode"/> applying to an entering permanent (CR 702.136a, 614.1c) — halts
    /// inside the interpreter with a pending question, so nothing further happens on this event
    /// until it is answered: the loop sees no event left and stops with the question raised.
    /// </summary>
    [RuleRef("614.1a")]
    private Applied Instead(GameState state, Effect effect, Candidate candidate, GameEvent current)
    {
        var outcome = _interpreter.Run(state, effect, Context(candidate.Source, candidate.Controller, current), IndexOf(state, current, candidate));
        return new Applied(null, outcome.State, outcome.Events);
    }

    /// <summary>
    /// CR 614.1c: the choice is made as the object enters, so the event survives and is offered on
    /// to the effects that have not applied yet (CR 616.1). The walk runs off the stack against the
    /// object as it will exist (CR 614.12), which is what the question carries as its source; when
    /// the answer comes, CR 608.2d writes it on the permanent that by then has arrived, and a later
    /// ability of that permanent reads it there.
    /// </summary>
    [RuleRef("614.1c")]
    private Applied Choosing(GameState state, Effect effect, Candidate candidate, GameEvent current)
    {
        var outcome = _interpreter.Run(state, effect, Context(candidate.Source, candidate.Controller, current), IndexOf(state, current, candidate));
        return new Applied(current, outcome.State, outcome.Events);
    }

    /// <summary>
    /// Whether the effect does nothing but choose a value: one <see cref="ChooseValue"/>, or a
    /// sequence of nothing else. Such an effect modifies no event, so CR 614.1a is the wrong
    /// reading of it (CR 608.2d).
    /// </summary>
    [RuleRef("608.2d")]
    private static bool Chooses(Effect effect) => effect switch
    {
        ChooseValue => true,
        Sequence sequence => sequence.Steps.Count > 0 && sequence.Steps.All(Chooses),
        _ => false,
    };

    /// <summary>CR 614.12: whether the proposed event is an object arriving on the battlefield.</summary>
    [RuleRef("614.12")]
    private static bool Arriving(GameEvent current) =>
        current is ObjectMoved moved && moved.To.Zone.Zone == Zone.Battlefield;

    /// <summary>
    /// CR 101.4: which ability of the source the instruction belongs to, so a question it asks can
    /// be found again there when the answer comes (E9c). CR 400.7 with 614.12: an object that is
    /// entering is a new object that exists only in the probe state the candidate was gathered
    /// from, so the index is looked up there — read from the state the event has not reached yet,
    /// every enters-replacement question would travel with no index and no answer could find it.
    /// </summary>
    [RuleRef("101.4")]
    private static int? IndexOf(GameState state, GameEvent current, Candidate candidate)
    {
        if (candidate.Source is not { } source || candidate.Ability is not { } ability)
            return null;

        if (!Probe(state, current).Objects.TryGetValue(source, out var obj))
            return null;

        var index = obj.Face.AbilityList.ToList().IndexOf(ability);
        return index >= 0 ? index : null;
    }

    /// <summary>
    /// CR 615.1: prevention removes damage from the event. A null amount is all of it, and an
    /// event with nothing left does not happen at all.
    /// </summary>
    [RuleRef("615.1")]
    private GameEvent? Prevent(GameState state, GameEvent current, Amount? amount, Candidate candidate)
    {
        if (current is not DamageDealt damage)
            return current;

        var prevented = amount is null
            ? damage.Amount
            : _query.Evaluate(state, amount, Context(candidate.Source, candidate.Controller, current));

        var left = Math.Max(0, damage.Amount - Math.Max(0, prevented));
        return left == 0 ? null : damage with { Amount = left };
    }

    /// <summary>CR 614.1: an effect that scales what happens rather than replacing it outright.</summary>
    private static GameEvent? Modify(GameEvent current, ModifyAmount modify)
    {
        switch (current)
        {
            case DamageDealt damage:
                var hit = Scaled(damage.Amount, modify);
                return hit == 0 ? null : damage with { Amount = hit };
            case CountersChanged counters:
                var delta = Scaled(counters.Delta, modify);
                return delta == 0 ? null : counters with { Delta = delta };
            case LifeChanged life:
                var change = Scaled(life.Delta, modify);
                return change == 0 ? null : life with { Delta = change };
            default:
                return current;
        }
    }

    /// <summary>The magnitude is scaled and the direction kept, so "twice as much life loss" still loses.</summary>
    private static int Scaled(int value, ModifyAmount modify)
    {
        var sign = value < 0 ? -1 : 1;
        return sign * Math.Max(0, (Math.Abs(value) * modify.Multiply) + modify.Add);
    }

    /// <summary>An entering-the-battlefield replacement changes the object that arrives, CR 614.12.</summary>
    [RuleRef("614.12")]
    private static GameEvent? Entering(GameEvent current, Func<GameObject, GameObject> change) =>
        current is ObjectMoved moved && moved.To.Zone.Zone == Zone.Battlefield
            ? moved with { To = change(moved.To) }
            : current;

    private static GameObject WithCounters(GameObject obj, string kind, int count) =>
        count <= 0 ? obj : obj with { Counters = obj.Counters.SetItem(kind, obj.Counters.GetValueOrDefault(kind) + count) };

    /// <summary>
    /// CR 614.12: "as this enters" choices that are part of the entering itself — face down
    /// (CR 701.36), and entering under another player's control (CR 613.1b).
    /// </summary>
    [RuleRef("614.12")]
    private GameObject EnteringAs(GameState state, GameObject obj, EntersAs how, Candidate candidate, GameEvent current)
    {
        var entering = how.FaceDown ? obj with { FaceDown = true } : obj;

        if (how.Transformed && entering.Definition.Faces.Count > 1)
            entering = entering with { FaceIndex = 1 };

        if (how.UnderControlOf is { } under)
        {
            var players = _query.Players(state, under, Context(candidate.Source, candidate.Controller, current));
            if (players.Count > 0)
                entering = entering with { InitialController = players[0] };
        }

        return entering;
    }
}
