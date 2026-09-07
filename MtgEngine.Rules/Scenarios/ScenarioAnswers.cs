using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Scenarios;

/// <summary>
/// The first legal answer to a question the game stopped to ask (CR 101.4), for a scenario that
/// was generated rather than written — a smoke scenario cannot name an option id, because the
/// ids are the game's objects and it has never seen them.
/// </summary>
/// <remarks>
/// This is deliberately the dullest answer available: a search takes the first card it may take,
/// a "may" says yes, a mode takes the first, a value takes the smallest. A generated scenario
/// asserts what the definition says always happens, so the answer must be the one that lets the
/// effect happen at all and must never be a judgement about which answer is better — that is a
/// hand-written scenario's business, and a scenario that carries its own
/// <see cref="AnswerStep"/> keeps control of every question in it.
/// </remarks>
[RuleRef("101.4")]
public static class ScenarioAnswers
{
    /// <summary>The option ids to answer this question with, or an empty list when there is nothing legal to say.</summary>
    public static IReadOnlyList<string> FirstLegal(PendingChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        // Fewer options than the question demands is a question with no legal answer at all — a
        // "sacrifice two" on a board holding one. Naming one anyway is refused (CR 601.2h), and a
        // refusal reads as the card failing; leaving it standing reports the position honestly.
        if (choice.Options.Count == 0 || choice.Options.Count < choice.Min)
            return [];

        return choice.Kind switch
        {
            // CR 608.2: an optional instruction. Saying yes is what makes the effect happen, and a
            // smoke scenario asserts the effect.
            ChoiceKinds.May => [Prefer(choice, "yes")],

            // CR 601.2b: the smallest value is the one a scenario can afford; ids that are not
            // numbers are ordered as the effect wrote them.
            ChoiceKinds.Value => [Smallest(choice)],

            // CR 701.23: a search finds as many cards as it may. The question allows none (Min is
            // zero), but a search that finds nothing asserts nothing, so the answer is the first
            // match and every further one the question's maximum still admits — "up to two basic
            // land cards" finds two, and a scenario can then say where both of them went.
            ChoiceKinds.Search => [.. choice.Options.Take(Finds(choice)).Select(o => o.Id)],

            _ => [.. choice.Options.Take(Count(choice)).Select(o => o.Id)],
        };
    }

    /// <summary>
    /// CR 701.23b: a search whose zones hold nothing the effect describes finds nothing, and
    /// finding nothing is a legal outcome rather than a question with no answer. The runner has to
    /// tell the two apart, because <see cref="FirstLegal"/> says "nothing to name" with the same
    /// empty list in both cases: a search offering no options at all, where naming none is within
    /// what the question allows, is answered with an empty answer and the resolution goes on.
    /// Anything else — a "sacrifice two" on a board holding one — is left standing, so the
    /// expectations fail on the position the game is actually in.
    /// </summary>
    [RuleRef("701.23b")]
    public static bool FailsToFind(PendingChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return choice.Kind == ChoiceKinds.Search && choice.Options.Count == 0 && choice.Min <= 0;
    }

    /// <summary>
    /// How many options to name: the fewest the question allows, and at least one, because a
    /// question with a minimum of none still has to be answered with something for the effect to
    /// go on. Never more than the question's maximum or the options it offered.
    /// </summary>
    private static int Count(PendingChoice choice)
    {
        var most = choice.Max <= 0 ? choice.Options.Count : Math.Min(choice.Max, choice.Options.Count);
        return Math.Min(Math.Max(choice.Min, 1), most);
    }

    /// <summary>
    /// CR 701.23a: how many cards a search takes — every one its maximum admits. A question with no
    /// maximum takes one, which is what a search for "a card" is and what every search took before
    /// there was a scenario that needed both halves of an "up to two".
    /// </summary>
    [RuleRef("701.23a")]
    private static int Finds(PendingChoice choice) =>
        choice.Max <= 0 ? 1 : Math.Min(choice.Max, choice.Options.Count);

    private static string Prefer(PendingChoice choice, string id) =>
        choice.Options.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase))?.Id
        ?? choice.Options[0].Id;

    private static string Smallest(PendingChoice choice)
    {
        string? best = null;
        var lowest = int.MaxValue;
        foreach (var option in choice.Options)
        {
            if (!int.TryParse(option.Id, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
                return choice.Options[0].Id;
            if (value >= lowest)
                continue;
            lowest = value;
            best = option.Id;
        }

        return best ?? choice.Options[0].Id;
    }
}
