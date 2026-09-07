using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Scenarios;

/// <summary>
/// Checks a scenario's expectations against the state the steps left. Every failure names what
/// was actually there, because "expected the graveyard" without "it is on the battlefield" is a
/// bug report nobody can act on.
/// </summary>
internal static class ScenarioExpectations
{
    internal static IReadOnlyList<ExpectationResult> Check(ScenarioRun run, IReadOnlyList<Expectation> expectations)
    {
        var results = new List<ExpectationResult>(expectations.Count);
        foreach (var expectation in expectations)
        {
            try
            {
                var (passed, actual) = One(run, expectation);
                results.Add(new ExpectationResult(expectation, passed, actual));
            }
            catch (ScenarioFailure failure)
            {
                results.Add(new ExpectationResult(expectation, false, failure.Message));
            }
        }

        return results;
    }

    private static (bool Passed, string? Actual) One(ScenarioRun run, Expectation expectation)
    {
        var state = run.State;
        switch (expectation)
        {
            case InZone e:
                {
                    var obj = Object(run, e.Card);
                    var owned = e.Owner is null || obj.Owner == run.Player(e.Owner);
                    var actual = owned ? $"{obj.Zone.Zone}" : $"{obj.Zone.Zone} owned by {state.Player(obj.Owner).Name}";
                    return (obj.Zone.Zone == e.Zone && owned, actual);
                }

            case Life e:
                {
                    var life = state.Player(run.Player(e.Player)).Life;
                    return (life == e.Value, life.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

            case PowerToughness e:
                {
                    var c = Now(run, e.Card);
                    return (c.Power == e.Power && c.Toughness == e.Toughness, $"{c.Power}/{c.Toughness}");
                }

            case CountersAre e:
                {
                    var counters = Object(run, e.Card).Counters;
                    var count = counters.TryGetValue(e.Counter, out var n) ? n : 0;
                    return (count == e.Count, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

            case TappedIs e:
                {
                    var tapped = Object(run, e.Card).Tapped;
                    return (tapped == e.Tapped, tapped ? "tapped" : "untapped");
                }

            case ZoneCount e:
                {
                    var count = Count(run, run.Player(e.Player), e.Zone);
                    return (count == e.Count, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

            case StackEmpty:
                {
                    var stack = state.In(ZoneKey.Shared(Zone.Stack));
                    return (stack.IsEmpty, stack.IsEmpty ? "empty" : $"{stack.Count} on the stack");
                }

            case GameOver e:
                return Over(run, e);

            case ControlledBy e:
                {
                    var controller = state.Player(Now(run, e.Card).Controller).Name;
                    return (string.Equals(controller, e.Player, StringComparison.OrdinalIgnoreCase), controller);
                }

            case HasKeywordNow e:
                {
                    var has = Now(run, e.Card).Has(e.Keyword);
                    return (has == e.Value, has ? $"has {e.Keyword}" : $"does not have {e.Keyword}");
                }

            case TypesAre e:
                return Types(run, e);

            case PlayerCountersAre e:
                {
                    var counters = state.Player(run.Player(e.Player)).Counters;
                    var count = counters.TryGetValue(e.Counter, out var n) ? n : 0;
                    return (count == e.Count, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

            case EventLogged e:
                return Logged(run, e);

            default:
                return (false, $"The runner cannot check {expectation.GetType().Name}.");
        }
    }

    /// <summary>CR 104.2: someone won, or CR 104.4, the game was a draw.</summary>
    [RuleRef("104.2")]
    private static (bool, string?) Over(ScenarioRun run, GameOver expectation)
    {
        var outcome = run.State.Outcome;
        if (outcome is null)
            return (false, "the game is still going");
        var names = string.Join(", ", outcome.Winners.Select(w => run.State.Player(w).Name));
        var actual = outcome.Draw ? "a draw" : names.Length == 0 ? "nobody won" : names;
        if (expectation.Winner is null)
            return (outcome.Draw || outcome.Winners.IsEmpty, actual);
        return (outcome.Winners.Any(w => string.Equals(run.State.Player(w).Name, expectation.Winner, StringComparison.OrdinalIgnoreCase)), actual);
    }

    /// <summary>CR 205: the types an object has right now, after every layer.</summary>
    [RuleRef("205")]
    private static (bool, string?) Types(ScenarioRun run, TypesAre expectation)
    {
        var c = Now(run, expectation.Card);
        var subtypes = string.Join(" ", c.Subtypes);
        var actual = subtypes.Length == 0 ? c.Types.ToString() : $"{c.Types} — {subtypes}";
        if (c.Types != expectation.Types)
            return (false, actual);
        if (expectation.Subtypes is null)
            return (true, actual);
        var wanted = expectation.Subtypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (wanted.SetEquals(c.Subtypes), actual);
    }

    private static (bool, string?) Logged(ScenarioRun run, EventLogged expectation)
    {
        var matching = run.Events.Where(e => string.Equals(e.GetType().Name, expectation.EventKind, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 0)
            return (false, "never logged");
        if (expectation.Card is null)
            return (true, $"logged {matching.Count} time(s)");

        var id = Object(run, expectation.Card).Id.ToString();
        var mentioned = matching.Any(e => (e.ToString() ?? string.Empty).Contains(id, StringComparison.Ordinal));
        return (mentioned, mentioned ? $"logged with {id}" : $"logged {matching.Count} time(s), never with {id}");
    }

    /// <summary>CR 400.1: a player's own zones are counted whole; a shared zone is counted by who controls what is in it.</summary>
    [RuleRef("400.1")]
    private static int Count(ScenarioRun run, PlayerId player, Zone zone)
    {
        var state = run.State;
        if (Zones.ZoneSystem.IsOwnerZone(zone))
            return state.In(ZoneKey.Of(zone, player)).Count;
        return state.In(ZoneKey.Shared(zone)).Count(id => run.Host.Engine.Characteristics.ControllerOf(state, id) == player);
    }

    private static GameObject Object(ScenarioRun run, string key)
    {
        var id = run.Object(key);
        return run.State.Objects.TryGetValue(id, out var obj)
            ? obj
            : throw new ScenarioFailure($"'{key}' is no longer in the game.");
    }

    private static Characteristics Now(ScenarioRun run, string key) =>
        run.Host.Engine.Characteristics.Of(run.State, Object(run, key).Id);
}
