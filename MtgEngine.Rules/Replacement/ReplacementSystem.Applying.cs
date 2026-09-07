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
        candidate.Shield is { } shield
            ? new Applied(Prevent(state, current, shield.Amount, candidate), state, [])
            : Perform(state, current, candidate.Ability!.With, candidate);

    private Applied Perform(GameState state, GameEvent current, Effect effect, Candidate candidate)
    {
        switch (effect)
        {
            case EntersTapped:
                return new Applied(Entering(current, obj => obj with { Tapped = true }), state, []);

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

    [RuleRef("614.1a")]
    private Applied Instead(GameState state, Effect effect, Candidate candidate, GameEvent current)
    {
        var outcome = _interpreter.Run(state, effect, Context(candidate.Source, candidate.Controller, current));
        return new Applied(null, outcome.State, outcome.Events);
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
