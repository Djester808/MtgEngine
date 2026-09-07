using MtgEngine.Cards.Pipeline.Agree;
using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// The two pilot commands. Every card here is made up, every answer is a fixture, and nothing
/// loads the corpus or reaches the network: what is under test is that the pilot submits through
/// the transport, collects through <see cref="TranslateStage"/>, decides through
/// <see cref="AgreeStage"/> and writes through <see cref="CardFileWriter"/> — rather than doing
/// any of it a second way of its own.
/// </summary>
public sealed class PilotTests
{
    private const string ScribeId = "00000000-0000-0000-0000-0000000000a1";
    private const string SageId = "00000000-0000-0000-0000-0000000000a2";
    private const string Opus = "claude-opus-5";
    private const string Sonnet = "claude-sonnet-5";

    [Fact]
    public async Task Submit_writes_one_batch_id_per_model_and_the_selection()
    {
        var harness = new Harness();
        harness.Client.PollsBeforeEnd = 99;

        var code = await Pilot.SubmitAsync(harness.Environment, harness.Cards, harness.OutDir, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(2, harness.Client.Submissions.Count);
        Assert.Equal(new[] { Opus, Sonnet }, harness.Client.Submissions.Select(s => s.Model).ToArray());
        Assert.All(harness.Client.Submissions, s => Assert.Equal(new[] { ScribeId, SageId }, s.OracleIds.ToArray()));

        var submission = PilotSubmission.Read(harness.OutDir);
        Assert.NotNull(submission);
        Assert.Equal(harness.Client.Submissions[0].BatchId, submission.Batches[Opus]);
        Assert.Equal(harness.Client.Submissions[1].BatchId, submission.Batches[Sonnet]);
        Assert.Equal(new[] { ScribeId, SageId }, submission.OracleIds.ToArray());

        // The store is what makes the collect command able to resume: the ids are recorded there
        // before anything waits on them, and not yet collected.
        foreach (var submitted in harness.Client.Submissions)
        {
            var stored = harness.Store.Batch(submitted.BatchId);
            Assert.NotNull(stored);
            Assert.False(stored.Collected);
        }
    }

    [Fact]
    public async Task Collect_exits_three_and_writes_nothing_while_a_batch_is_still_running()
    {
        var harness = new Harness();
        harness.Client.PollsBeforeEnd = 99;
        await Pilot.SubmitAsync(harness.Environment, harness.Cards, harness.OutDir, CancellationToken.None);

        var code = await Pilot.CollectAsync(harness.Environment, harness.OutDir, CancellationToken.None);

        Assert.Equal(Pilot.NotEnded, code);
        Assert.False(File.Exists(Path.Combine(harness.OutDir, "report.md")));
        Assert.Null(harness.Writer.Load(ScribeId));
        Assert.Null(harness.Store.Reading(ScribeId, Opus, Harness.PromptVersion));

        // One status check per batch and no loop around it: a batch is never slept on.
        Assert.Equal(2, harness.Client.PollCount);
    }

    [Fact]
    public async Task Collect_on_ended_batches_writes_the_statuses_and_a_report_that_matches_the_readings()
    {
        var harness = new Harness();
        harness.Client.Answer(Opus, ScribeId, Json(Drawing(ScribeId, "Test Scribe")));
        harness.Client.Answer(Sonnet, ScribeId, Json(Drawing(ScribeId, "Test Scribe")));
        harness.Client.Answer(Opus, SageId, Json(Drawing(SageId, "Test Sage")));
        harness.Client.Answer(Sonnet, SageId, Json(Milling(SageId, "Test Sage")));
        await Pilot.SubmitAsync(harness.Environment, harness.Cards, harness.OutDir, CancellationToken.None);

        var code = await Pilot.CollectAsync(harness.Environment, harness.OutDir, CancellationToken.None);

        Assert.Equal(0, code);

        // Two readings that say the same thing agree; two that differ are Disputed and both are kept.
        var agreed = harness.Writer.Load(ScribeId);
        Assert.NotNull(agreed);
        Assert.Equal(ImplementationStatus.Translated, agreed.Status);
        Assert.True(agreed.Source is TranslatedProvenance { Agreed: true });

        var disputed = harness.Writer.Load(SageId);
        Assert.NotNull(disputed);
        Assert.Equal(ImplementationStatus.Disputed, disputed.Status);
        Assert.Equal(2, disputed.Candidates?.Count);

        var report = await File.ReadAllTextAsync(Path.Combine(harness.OutDir, "report.md"), CancellationToken.None);
        Assert.Contains("faces: 2", report, StringComparison.Ordinal);
        Assert.Contains("## " + Opus, report, StringComparison.Ordinal);
        Assert.Contains("- parsed: 2/2", report, StringComparison.Ordinal);
        Assert.Contains("- lint clean: 2/2", report, StringComparison.Ordinal);
        Assert.Contains("- parse failures: 0/2 (0.0%)", report, StringComparison.Ordinal);
        Assert.Contains("- lint failures: 0/2 (0.0%)", report, StringComparison.Ordinal);
        Assert.Contains("- with Unsupported markers: 0", report, StringComparison.Ordinal);

        // The fake bills 100 input, 20 output and 9,000 cache-read tokens per answer.
        Assert.Contains("- tokens: input 200, cache read 18,000, cache write 0, output 40", report, StringComparison.Ordinal);
        Assert.Contains("- estimated batch cost: $", report, StringComparison.Ordinal);
        Assert.Contains("- both models lint clean: 2/2", report, StringComparison.Ordinal);
        Assert.Contains("agreed (identical after stripping printed text): 1/2 (50.0%)", report, StringComparison.Ordinal);
        Assert.Contains("- card files written: 2/2", report, StringComparison.Ordinal);
        Assert.Contains("- Test Sage: ", report, StringComparison.Ordinal);
        Assert.DoesNotContain("- Test Scribe: ", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reading_that_lints_dirty_is_repaired_once_and_the_repair_is_what_is_judged()
    {
        var harness = new Harness();
        harness.Client.Answer(Opus, ScribeId, Json(Definition(ScribeId, "Test Scribe", new Unsupported("no primitive for this line", "Do the thing."))));
        harness.Client.Answer(Sonnet, ScribeId, Json(Drawing(ScribeId, "Test Scribe")));
        harness.Client.RepairWith(Opus, ScribeId, Json(Drawing(ScribeId, "Test Scribe")));
        harness.Client.Answer(Opus, SageId, Json(Drawing(SageId, "Test Sage")));
        harness.Client.Answer(Sonnet, SageId, Json(Drawing(SageId, "Test Sage")));
        await Pilot.SubmitAsync(harness.Environment, harness.Cards, harness.OutDir, CancellationToken.None);

        var code = await Pilot.CollectAsync(harness.Environment, harness.OutDir, CancellationToken.None);

        Assert.Equal(0, code);
        var repair = Assert.Single(harness.Client.Repairs);
        Assert.Equal(Opus, repair.Model);
        Assert.Contains("L001", repair.Problem, StringComparison.Ordinal);

        // The repaired reading is the one agreement judged, so the card is Translated, not Disputed.
        var file = harness.Writer.Load(ScribeId);
        Assert.NotNull(file);
        Assert.Equal(ImplementationStatus.Translated, file.Status);
        Assert.NotNull(harness.Store.RepairReading(ScribeId, Opus, Harness.PromptVersion));

        var report = await File.ReadAllTextAsync(Path.Combine(harness.OutDir, "report.md"), CancellationToken.None);
        Assert.Contains("- repaired: 1", report, StringComparison.Ordinal);
        Assert.Contains("agreed (identical after stripping printed text): 2/2 (100.0%)", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// A single reading is linted on its own as Translated, never as Disputed: linting it as
    /// Disputed raises L013 ("a Disputed file must keep both readings") on every reading, and no
    /// card could ever agree (seam-requests/DECISIONS.md, P3).
    /// </summary>
    [Fact]
    public async Task A_lone_clean_reading_is_never_linted_as_disputed()
    {
        var harness = new Harness();
        harness.Client.Answer(Opus, ScribeId, Json(Drawing(ScribeId, "Test Scribe")));
        harness.Client.Answer(Sonnet, ScribeId, Json(Milling(ScribeId, "Test Scribe")));
        harness.Client.Answer(Opus, SageId, Json(Drawing(SageId, "Test Sage")));
        harness.Client.Answer(Sonnet, SageId, Json(Drawing(SageId, "Test Sage")));
        await Pilot.SubmitAsync(harness.Environment, harness.Cards, harness.OutDir, CancellationToken.None);

        await Pilot.CollectAsync(harness.Environment, harness.OutDir, CancellationToken.None);

        var report = await File.ReadAllTextAsync(Path.Combine(harness.OutDir, "report.md"), CancellationToken.None);
        Assert.DoesNotContain("L013", report, StringComparison.Ordinal);
        Assert.Contains("- lint clean: 2/2", report, StringComparison.Ordinal);

        // Disputed on a difference, not on a lint failure: both readings were clean.
        Assert.Contains(Opus + ": clean", report, StringComparison.Ordinal);
        Assert.Contains(Sonnet + ": clean", report, StringComparison.Ordinal);
    }

    private static string Json(CardDefinition definition) => CardFileSerializer.WriteDefinition(definition);

    private static CardDefinition Drawing(string oracleId, string name) => Definition(oracleId, name, new Draw(new Fixed(1)));

    private static CardDefinition Milling(string oracleId, string name) => Definition(oracleId, name, new Mill(new Fixed(1)));

    /// <summary>A made-up creature whose one ability is whatever the caller wants to have happen.</summary>
    private static CardDefinition Definition(string oracleId, string name, Effect effect) => new(
        OracleId: oracleId,
        Name: name,
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: name,
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Creature, Subtypes: ["Human", "Wizard"]),
                ManaCost: "{1}{U}",
                Power: new PrintedStat("1"),
                Toughness: new PrintedStat("2"),
                Abilities:
                [
                    new TriggeredAbility(new AtStep(Step.Upkeep, new You()), effect) { Text = Text },
                ],
                OracleText: Text),
        ]);

    private const string Text = "At the beginning of your upkeep, draw a card.";

    /// <summary>Two made-up cards, a scratch store, a scratch cards directory and a fake transport.</summary>
    private sealed class Harness
    {
        public const string PromptVersion = "pilot-test-v1";

        public Harness()
        {
            var scratch = Path.Combine(AppContext.BaseDirectory, "pilot-tests", Guid.NewGuid().ToString("n"));
            OutDir = Path.Combine(scratch, "out");
            Store = new TranslationStore(Path.Combine(scratch, "translations"));
            Writer = new CardFileWriter(Path.Combine(scratch, "cards"));
            Cards = [Card(ScribeId, "Test Scribe"), Card(SageId, "Test Sage")];
            Environment = new PilotEnvironment(
                Client,
                Store,
                Writer,
                ids => [.. ids.Select(id => Cards.First(c => string.Equals(c.OracleId, id, StringComparison.Ordinal)))],
                Pilot.Models,
                PromptVersion,
                Log);
        }

        public PilotFakeClient Client { get; } = new();

        public TranslationStore Store { get; }

        public CardFileWriter Writer { get; }

        public IReadOnlyList<CorpusCard> Cards { get; }

        public PilotEnvironment Environment { get; }

        public string OutDir { get; }

        public StringWriter Log { get; } = new();

        private static CorpusCard Card(string oracleId, string name) => new(
            oracleId,
            name,
            "normal",
            [new CorpusFace(name, "Creature", "{1}{U}", Text, "1", "2", null, null)],
            [],
            []);
    }
}
