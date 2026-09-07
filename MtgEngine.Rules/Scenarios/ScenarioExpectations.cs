using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
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

            case ManaPoolIs e:
                return Pool(run, e);

            case AttachedToIs e:
                return Attached(run, e);

            case EventLogged e:
                return Logged(run, e);

            case Shuffled e:
                return WasShuffled(run, e);

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

    /// <summary>
    /// CR 106.4: a mana pool is a bag of symbols and not a sequence, so the comparison is a count
    /// per colour and the order the mana arrived in says nothing. Restricted mana (CR 106.6a)
    /// counts as the colour it is: an expectation that wants to see the restriction is a second
    /// record, not a spelling of this one.
    /// </summary>
    [RuleRef("106.4")]
    private static (bool, string?) Pool(ScenarioRun run, ManaPoolIs expectation)
    {
        var pool = Bag(run.State.Player(run.Player(expectation.Player)).Mana);
        var wanted = Bag(ScenarioRun.ParseMana(expectation.Mana));
        return (pool == wanted, Symbols(pool));
    }

    private static (int W, int U, int B, int R, int G, int C) Bag(ManaPool pool)
    {
        var (w, u, b, r, g, c) = (pool.White, pool.Blue, pool.Black, pool.Red, pool.Green, pool.Colorless);
        foreach (var block in pool.Restricted)
        {
            switch (block.Color)
            {
                case Cards.Color.White:
                    w += block.Amount;
                    break;
                case Cards.Color.Blue:
                    u += block.Amount;
                    break;
                case Cards.Color.Black:
                    b += block.Amount;
                    break;
                case Cards.Color.Red:
                    r += block.Amount;
                    break;
                case Cards.Color.Green:
                    g += block.Amount;
                    break;
                default:
                    c += block.Amount;
                    break;
            }
        }

        return (w, u, b, r, g, c);
    }

    private static string Symbols((int W, int U, int B, int R, int G, int C) bag)
    {
        var text = string.Concat(
            Repeat("W", bag.W), Repeat("U", bag.U), Repeat("B", bag.B),
            Repeat("R", bag.R), Repeat("G", bag.G), Repeat("C", bag.C));
        return text.Length == 0 ? "an empty pool" : text;

        static string Repeat(string symbol, int n) => n <= 0 ? string.Empty : string.Concat(Enumerable.Repeat("{" + symbol + "}", n));
    }

    /// <summary>
    /// CR 301.5 and 303.4f: an Aura, Equipment or Fortification is attached to something, and the
    /// something is an object still in the game. Being on the battlefield is not the same claim —
    /// CR 704.5m puts an unattached Aura into its owner's graveyard — so an unattached permanent
    /// fails here and says so rather than reporting a zone that is about to change.
    /// </summary>
    [RuleRef("303.4f")]
    private static (bool, string?) Attached(ScenarioRun run, AttachedToIs expectation)
    {
        var obj = Object(run, expectation.Card);
        if (obj.AttachedTo is not { } attached)
            return (false, $"not attached, in {obj.Zone.Zone}");
        var host = run.State.Objects.TryGetValue(attached, out var found)
            ? found.Definition.Name
            : "an object that has left the game";
        return (attached == run.Object(expectation.To), $"attached to {host}");
    }

    /// <summary>CR 701.20: a reorder of that player's library that says it was a shuffle.</summary>
    private static (bool, string?) WasShuffled(ScenarioRun run, Shuffled expectation)
    {
        var library = ZoneKey.Of(Zone.Library, run.Player(expectation.Player));
        // The opening shuffle of CR 103.2 is the setup's, not the scenario's: only a shuffle after the steps began counts.
        var shuffles = run.Events.Skip(run.StepsBeganAt).Count(e => e is ZoneReordered { Shuffle: true } reordered && reordered.Zone == library);
        return shuffles > 0 ? (true, $"shuffled {shuffles} time(s)") : (false, "never shuffled");
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
