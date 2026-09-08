using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Choices;

/// <summary>
/// The bounds a numeric question is answered within, CR 601.2b. Either end may be open: a card
/// that asks for "a number 0 or greater" has a low and no high, and one that asks for "a number
/// you choose" has neither.
/// </summary>
/// <param name="Low">The least number the answer may be, or none for no lower bound.</param>
/// <param name="High">The greatest number the answer may be, or none for no upper bound.</param>
[RuleRef("601.2b")]
public readonly record struct NumberRange(int? Low, int? High)
{
    /// <summary>Whether a number is inside these bounds, CR 601.2b.</summary>
    public bool Admits(int number) => (Low is not { } low || number >= low) && (High is not { } high || number <= high);
}

/// <summary>
/// A question whose answer is a number and not one of a list, CR 601.2b with 101.4.
/// <para>
/// Every other question carries its answers as options and <c>ChoiceSystem.Validate</c> checks the
/// answer against them. That makes an unbounded choice — "choose a number 0 or greater" — a
/// question nothing can answer, and a wide bounded one a question carrying an option per number;
/// both were refused by name rather than asked. This is the other channel: the question carries a
/// <see cref="NumberRange"/> instead of an option list, the answer is the number written out, and
/// the bounds are checked where the option list would have been.
/// </para>
/// <para>
/// The range rides in <see cref="ChoiceContext.Extra"/> rather than in two more fields on
/// <see cref="PendingChoice"/>, so nothing outside <c>Choices/**</c> has to move for it to exist:
/// the keys, the writing and the reading are all here, and no raiser spells them a second time.
/// The answer arrives as the same string the arm that asked reads back — the chosen option the
/// resumption road hands the interpreter has the number as its id — so an arm that already stores
/// its answer by key needs no new road for it (<c>seam-requests/E4h.md</c>).
/// </para>
/// </summary>
[RuleRef("601.2b")]
public static class ChoiceNumbers
{
    /// <summary>
    /// The mark that says this question's answer is a number, CR 601.2b. Its presence is the whole
    /// signal; the value is the range written out for a reader of the state, and nothing parses it.
    /// </summary>
    public const string Numeric = "numeric";

    /// <summary>The least number the answer may be, CR 601.2b. Absent means no lower bound.</summary>
    public const string NumericLow = "numeric-low";

    /// <summary>The greatest number the answer may be, CR 601.2b. Absent means no upper bound.</summary>
    public const string NumericHigh = "numeric-high";

    /// <summary>
    /// The question's context with the numeric channel opened on it, CR 601.2b. An arm that wants a
    /// number asks with no options and this context; everything else about the question — its kind,
    /// its prompt, its <c>storeAs</c> — is unchanged, so the answer comes back where the arm reads it.
    /// </summary>
    [RuleRef("601.2b")]
    public static ChoiceContext Ranged(ChoiceContext context, int? low, int? high)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (low is { } l && high is { } h && l > h)
            throw new RefusedException("That question has no number in it: its lowest is above its highest.", "601.2b");

        var extra = context.Extra.SetItem(Numeric, Written(low, high));
        extra = low is { } lo ? extra.SetItem(NumericLow, Text(lo)) : extra.Remove(NumericLow);
        extra = high is { } hi ? extra.SetItem(NumericHigh, Text(hi)) : extra.Remove(NumericHigh);
        return context with { Extra = extra };
    }

    /// <summary>
    /// A whole numeric question, for a raiser that has no <c>Ask</c> of its own, CR 601.2b. The
    /// option list is empty on purpose: the answer is checked against the range instead.
    /// </summary>
    [RuleRef("601.2b")]
    public static PendingChoice Question(
        ChoiceId id,
        PlayerId player,
        string kind,
        string prompt,
        int? low,
        int? high,
        ChoiceContext? context = null) =>
        new(
            id,
            player,
            kind,
            prompt,
            [],
            1,
            1,
            Ranged(context ?? new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty), low, high));

    /// <summary>
    /// The range a question carries, or none when it is answered from its options, CR 601.2b. A
    /// bound that is not a number is no bound: the mark is what opens the channel, and a broken
    /// bound must not silently become a different question than the one that was asked, so it
    /// widens rather than narrows.
    /// </summary>
    [RuleRef("601.2b")]
    public static NumberRange? RangeOf(PendingChoice pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var extra = pending.Context.Extra;
        return extra.ContainsKey(Numeric) ? new NumberRange(Bound(extra, NumericLow), Bound(extra, NumericHigh)) : null;
    }

    /// <summary>
    /// The number a numeric answer names, CR 601.2b: one answer, written in decimal, inside the
    /// bounds. Anything else is refused with the rule the question's kind cites, so a client is
    /// told why rather than having a number invented for it (CR 101.4).
    /// </summary>
    [RuleRef("101.4")]
    internal static int Answered(NumberRange range, ImmutableList<string> answer, string rule)
    {
        if (answer.Count != 1)
            throw new RefusedException("That question is answered with one number.", rule);
        if (!int.TryParse(answer[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            throw new RefusedException("That answer is not a number.", rule);
        if (!range.Admits(number))
            throw new RefusedException($"That number is outside {Written(range.Low, range.High)}.", rule);
        return number;
    }

    private static int? Bound(ImmutableDictionary<string, string> extra, string key) =>
        extra.TryGetValue(key, out var raw)
        && int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>The range as a player is told it, CR 601.2b, with an open end written as nothing.</summary>
    private static string Written(int? low, int? high) =>
        (low is { } l ? Text(l) : string.Empty) + ".." + (high is { } h ? Text(h) : string.Empty);

    private static string Text(int number) => number.ToString(CultureInfo.InvariantCulture);
}
