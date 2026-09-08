using MtgEngine.Cards.Pipeline.Agree;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// Wave 27 (P26): the report's "agreed among all-clean" line, which counted every agreed card over
/// the cards every model linted clean and so could print a ratio above one
/// (seam-requests/P25.md § 3). Nothing here reaches the network: the report is built off results
/// made by hand.
/// </summary>
public sealed partial class PilotTests
{
    /// <summary>
    /// A card agrees on a majority of the readings that parsed and linted clean, so a third reading
    /// with lint errors leaves the card agreed and not all-clean. The numerator has to ask for both
    /// or it counts a card its own denominator excluded: two agreed cards over one all-clean card
    /// used to print "2/1 (200.0%)", which is not a percentage of anything.
    /// </summary>
    [Fact]
    public void The_agreed_among_all_clean_line_counts_only_the_cards_every_model_linted_clean()
    {
        var harness = new Harness();
        var clean = new LintReport([]);
        var dirty = new LintReport([new LintFinding(LintSeverity.Error, "L001", "no record for this clause", "$.faces[0]")]);

        var report = PilotReport.Build(
            [
                Result(ScribeId, "Test Scribe", ImplementationStatus.Translated, new Dictionary<string, LintReport?> { [Opus] = clean, [Sonnet] = clean, [Fable] = clean }),
                Result(SageId, "Test Sage", ImplementationStatus.Translated, new Dictionary<string, LintReport?> { [Opus] = clean, [Sonnet] = clean, [Fable] = dirty }),
            ],
            harness.Environment,
            Harness.PromptVersion,
            models: []);

        // Both cards agreed, and one of them is the one every model linted clean.
        Assert.Contains("- all models lint clean: 1/2", report, StringComparison.Ordinal);
        Assert.Contains("- agreed (identical after stripping printed text): 2/2", report, StringComparison.Ordinal);
        Assert.Contains("- agreed among all-clean: 1/1 (100.0%)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("agreed among all-clean: 2/1", report, StringComparison.Ordinal);
    }

    /// <summary>A card the pilot read, with a verdict and one lint report per model and no file written.</summary>
    private static PilotCardResult Result(string oracleId, string name, ImplementationStatus status, IReadOnlyDictionary<string, LintReport?> lint) =>
        new(
            Harness.Card(oracleId, name),
            [],
            new AgreeResult(oracleId, name, status, File: null, lint, CleanModels: [.. lint.Keys], Difference: null),
            Written: null);
}
