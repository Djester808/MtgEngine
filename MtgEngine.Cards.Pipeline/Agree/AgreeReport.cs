using System.Globalization;
using System.Text;

namespace MtgEngine.Cards.Pipeline.Agree;

/// <summary>One shape of disagreement and how many cards it cost, keyed by the property that differed.</summary>
public sealed record DifferenceGroup(string Property, int Cards, string Example);

/// <summary>
/// What a batch of agreement decisions came to, and — the point of it — the remaining first
/// differences grouped by the property that differed, largest first. A list of 37 sentences is
/// read once and forgotten; "who: 9 cards" is the next canonicalisation rule, or the next line of
/// the translator's reference. The pilot's own numbers were only usable because someone grouped
/// them by hand, so the stage does it.
/// </summary>
public sealed record AgreeReport(
    int Cards,
    int Agreed,
    int Disputed,
    int Untranslated,
    IReadOnlyList<DifferenceGroup> Differences)
{
    public static AgreeReport Of(IEnumerable<AgreeResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var all = results.ToList();
        var groups = new Dictionary<string, (int Cards, string Example)>(StringComparer.Ordinal);
        foreach (var difference in all.Select(r => r.Difference).OfType<string>())
        {
            var property = PropertyOf(difference);
            groups[property] = groups.TryGetValue(property, out var seen)
                ? (seen.Cards + 1, seen.Example)
                : (1, difference);
        }

        return new AgreeReport(
            all.Count,
            all.Count(r => r.Status == ImplementationStatus.Translated),
            all.Count(r => r.Status == ImplementationStatus.Disputed),
            all.Count(r => r.Status == ImplementationStatus.Untranslated),
            [.. groups
                .OrderByDescending(g => g.Value.Cards)
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new DifferenceGroup(g.Key, g.Value.Cards, g.Value.Example))]);
    }

    /// <summary>
    /// The last segment of a difference's path, without its index: the property two readings
    /// disagreed about. <c>faces[0].abilities[1].do.who: … vs …</c> is <c>who</c>;
    /// <c>faces[0].abilities: 2 items vs 1 items</c> is <c>abilities</c>.
    /// </summary>
    public static string PropertyOf(string difference)
    {
        ArgumentNullException.ThrowIfNull(difference);
        var colon = difference.IndexOf(": ", StringComparison.Ordinal);
        var path = colon < 0 ? difference : difference[..colon];
        var dot = path.LastIndexOf('.');
        var last = dot < 0 ? path : path[(dot + 1)..];
        var bracket = last.IndexOf('[', StringComparison.Ordinal);
        return bracket < 0 ? last : last[..bracket];
    }

    /// <summary>The report as the pipeline prints it: the counts, then one line per shape.</summary>
    public string Text()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{Cards} cards: {Agreed} agreed, {Disputed} disputed, {Untranslated} untranslated"));
        foreach (var group in Differences)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {group.Property}: {group.Cards}  e.g. {group.Example}"));
        return sb.ToString();
    }
}
