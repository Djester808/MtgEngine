using System.Globalization;
using System.Text;

namespace MtgEngine.Cards.Pipeline.Agree;

/// <summary>
/// One shape of disagreement and how many cards it cost, keyed by the property that differed.
/// <paramref name="Example"/> is the whole difference line — the path and both readings' values,
/// so the shape is readable without opening a card file — and <paramref name="ExampleCard"/> names
/// the card it came from for when it is not.
/// </summary>
public sealed record DifferenceGroup(string Property, int Cards, string Example, string ExampleCard, string Left, string Right);

/// <summary>How many cards two models agreed on — the per-pair count a three-reading run is read by.</summary>
public sealed record PairAgreement(string Left, string Right, int Agreed);

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
    IReadOnlyList<DifferenceGroup> Differences,
    IReadOnlyList<PairAgreement>? Pairs = null)
{
    /// <summary>How many cards each pair of models agreed on, largest first. One pair for a two-model run; three for a three-model one.</summary>
    public IReadOnlyList<PairAgreement> AgreedByPair => Pairs ?? [];

    public static AgreeReport Of(IEnumerable<AgreeResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var all = results.ToList();
        var pairs = new Dictionary<(string Left, string Right), int>();
        foreach (var result in all.Where(r => r.Agreed))
        {
            var models = result.AgreedBy;
            for (var i = 0; i < models.Count; i++)
            {
                for (var j = i + 1; j < models.Count; j++)
                {
                    var key = (models[i], models[j]);
                    pairs[key] = pairs.TryGetValue(key, out var seen) ? seen + 1 : 1;
                }
            }
        }

        var groups = new Dictionary<string, (int Cards, string Example, string Card)>(StringComparer.Ordinal);
        foreach (var result in all.Where(r => r.Difference is not null))
        {
            var difference = result.Difference!;
            var property = PropertyOf(difference);
            groups[property] = groups.TryGetValue(property, out var seen)
                ? (seen.Cards + 1, seen.Example, seen.Card)
                : (1, difference, result.Name);
        }

        return new AgreeReport(
            all.Count,
            all.Count(r => r.Status == ImplementationStatus.Translated),
            all.Count(r => r.Status == ImplementationStatus.Disputed),
            all.Count(r => r.Status == ImplementationStatus.Untranslated),
            [.. groups
                .OrderByDescending(g => g.Value.Cards)
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new DifferenceGroup(
                    g.Key,
                    g.Value.Cards,
                    g.Value.Example,
                    g.Value.Card,
                    Side(g.Value.Example, left: true),
                    Side(g.Value.Example, left: false)))],
            [.. pairs
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key.Left, StringComparer.Ordinal)
                .ThenBy(p => p.Key.Right, StringComparer.Ordinal)
                .Select(p => new PairAgreement(p.Key.Left, p.Key.Right, p.Value))]);
    }

    /// <summary>
    /// One side of an example's <c>a vs b</c>, so a caller that renders the pair in two columns
    /// does not have to re-split the line. Empty when the line has no pair, which is the shape a
    /// count difference takes (<c>abilities: 2 items vs 1 items</c> splits, <c>$</c> alone does not).
    /// </summary>
    private static string Side(string example, bool left)
    {
        var colon = example.IndexOf(": ", StringComparison.Ordinal);
        var values = colon < 0 ? example : example[(colon + 2)..];
        var vs = values.IndexOf(" vs ", StringComparison.Ordinal);
        if (vs < 0)
            return string.Empty;
        return left ? values[..vs] : values[(vs + 4)..];
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
        if (AgreedByPair.Count > 0)
        {
            sb.AppendLine("  agreed by pair: " + string.Join(", ", AgreedByPair.Select(p =>
                string.Create(CultureInfo.InvariantCulture, $"{p.Left}+{p.Right}: {p.Agreed}"))));
        }

        foreach (var group in Differences)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {group.Property}: {group.Cards}  e.g. {group.Example}"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"      on {group.ExampleCard}"));
        }

        return sb.ToString();
    }
}
