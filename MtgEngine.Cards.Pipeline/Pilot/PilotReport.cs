using System.Globalization;
using System.Text;
using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline;

/// <summary>
/// The pilot's output for a person: per model what parsed, what linted, what it cost and where
/// it needed a primitive it did not have; then the agreement rate, which is the number the
/// decision point in CARD_ENGINE_PLAN.md turns on, and the disagreements themselves.
///
/// Every number here is counted off the stage's readings and the agreement stage's verdicts, so
/// the report cannot say something different from what was written to <c>cards/</c>.
/// </summary>
public static class PilotReport
{
    /// <summary>List prices per million tokens; batch requests are billed at half, cache reads at a tenth, cache writes at 1.25x.</summary>
    private static readonly IReadOnlyDictionary<string, (decimal Input, decimal Output)> Prices = new Dictionary<string, (decimal, decimal)>(StringComparer.Ordinal)
    {
        ["claude-opus-5"] = (5m, 25m),
        ["claude-sonnet-5"] = (2m, 10m),
    };

    public static string Build(IReadOnlyList<PilotCardResult> results, PilotEnvironment env, string promptVersion)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(env);

        var count = results.Count;
        var sb = new StringBuilder();
        sb.AppendLine("# Pilot report");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"faces: {count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"prompt version: {promptVersion}");
        sb.AppendLine();

        foreach (var model in env.Models)
            Model(sb, results, env, promptVersion, model, count);

        Agreement(sb, results, env, count);
        return sb.ToString();
    }

    private static void Model(StringBuilder sb, IReadOnlyList<PilotCardResult> results, PilotEnvironment env, string promptVersion, string model, int count)
    {
        var readings = results.Select(r => Reading(r, model)).ToList();
        var parsed = readings.Count(r => r?.Definition is not null);
        var clean = readings.Count(r => r is { Definition: not null, Lint.HasErrors: false });
        var lintFailed = readings.Count(r => r?.Definition is not null && r.Lint is { HasErrors: true });
        var unsupported = readings.Count(r => r?.Lint?.Findings.Any(f => string.Equals(f.Code, "L001", StringComparison.Ordinal)) == true);
        var repaired = readings.Count(r => r?.Repaired == true);

        var usage = results.Aggregate(default(Usage), (total, r) => total + Tokens(env, r.Card.OracleId, model, promptVersion));
        var price = Prices.TryGetValue(model, out var p) ? p : (Input: 0m, Output: 0m);
        var cost = ((usage.Input * price.Input)
            + (usage.CacheRead * price.Input * 0.1m)
            + (usage.CacheWrite * price.Input * 1.25m)
            + (usage.Output * price.Output)) / 1_000_000m * 0.5m;

        sb.AppendLine(CultureInfo.InvariantCulture, $"## {model}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- parsed: {parsed}/{count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- lint clean: {clean}/{count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- parse failures: {count - parsed}/{count} ({Percent(count - parsed, count)})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- lint failures: {lintFailed}/{count} ({Percent(lintFailed, count)})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- with Unsupported markers: {unsupported}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- repaired: {repaired}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- tokens: input {usage.Input:N0}, cache read {usage.CacheRead:N0}, cache write {usage.CacheWrite:N0}, output {usage.Output:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- estimated batch cost: ${cost:F2} (${cost / Math.Max(1, count):F3} per face)");

        var codes = readings
            .SelectMany(r => r?.Lint?.Findings ?? [])
            .GroupBy(f => f.Code, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count());
        sb.AppendLine("- lint findings by code: " + string.Join(", ", codes.Select(g => FormattableString.Invariant($"{g.Key}x{g.Count()}"))));

        var reasons = readings
            .SelectMany(r => (r?.Lint?.Findings ?? []).Where(f => string.Equals(f.Code, "L001", StringComparison.Ordinal)))
            .GroupBy(f => f.Message, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Take(15);
        foreach (var g in reasons)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  - {g.Count()}x {g.Key}");
        sb.AppendLine();
    }

    private static void Agreement(StringBuilder sb, IReadOnlyList<PilotCardResult> results, PilotEnvironment env, int count)
    {
        var agreed = results.Count(r => r.Agreement.Agreed);
        var bothClean = results.Count(r => env.Models.All(m => r.Agreement.Lint.TryGetValue(m, out var lint) && lint is { HasErrors: false }));
        var written = results.Count(r => r.Written is not null);

        sb.AppendLine("## Agreement");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- both models lint clean: {bothClean}/{count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- agreed (identical after stripping printed text): {agreed}/{count} ({Percent(agreed, count)})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- agreed among both-clean: {agreed}/{bothClean} ({Percent(agreed, bothClean)})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- card files written: {written}/{count}");
        sb.AppendLine();

        // The shapes, largest first, with one example each: a list of thirty-seven sentences is
        // read once and forgotten, and "who: 9 cards" is the next canonicalisation rule
        // (seam-requests/P6.md § 1).
        sb.Append(Agree.AgreeReport.Of(results.Select(r => r.Agreement)).Text());
        sb.AppendLine();

        sb.AppendLine("## Disagreements (first 40)");
        foreach (var r in results.Where(r => !r.Agreement.Agreed).Take(40))
        {
            var verdicts = string.Join(" | ", env.Models.Select(m => m + ": " + Verdict(r, m)));
            sb.Append("- ").Append(r.Card.Name).Append(": ").AppendLine(verdicts);
            if (r.Agreement.Difference is not null)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - first difference: {r.Agreement.Difference}");
        }
    }

    private static string Verdict(PilotCardResult result, string model)
    {
        var reading = Reading(result, model);
        if (reading?.Definition is null)
            return "parse fail";
        var lint = reading.Lint;
        if (lint is null || !lint.HasErrors)
            return "clean";
        var codes = lint.Findings.Where(f => f.Severity == LintSeverity.Error).Select(f => f.Code).Distinct(StringComparer.Ordinal);
        return "lint " + string.Join(",", codes);
    }

    private static TranslationReading? Reading(PilotCardResult result, string model) =>
        result.Readings.FirstOrDefault(r => string.Equals(r.Model, model, StringComparison.Ordinal));

    /// <summary>What one card cost one model, first answer plus its repair round if there was one.</summary>
    private static Usage Tokens(PilotEnvironment env, string oracleId, string model, string promptVersion)
    {
        var total = default(Usage);
        foreach (var stored in new[] { env.Store.Reading(oracleId, model, promptVersion), env.Store.RepairReading(oracleId, model, promptVersion) })
        {
            if (stored is not null)
                total += new Usage(stored.InputTokens, stored.OutputTokens, stored.CacheReadTokens, stored.CacheWriteTokens);
        }

        return total;
    }

    private static string Percent(int part, int whole) =>
        FormattableString.Invariant($"{100.0 * part / Math.Max(1, whole):F1}%");

    /// <summary>The four token counts, summed across readings.</summary>
    private readonly record struct Usage(long Input, long Output, long CacheRead, long CacheWrite)
    {
        public static Usage operator +(Usage a, Usage b) =>
            new(a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite);
    }
}
