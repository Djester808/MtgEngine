using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// The translation stage over a fake client and hand-written answers: what it submits, what it
/// stores, when it repairs, and what a second run costs. Both cards are invented — no real card
/// name appears here, so nothing in this file can be mistaken for a card in C#
/// (CARD_ENGINE_PLAN.md, rule 1).
/// </summary>
public sealed class TranslateStageTests
{
    private const string CleanId = "f1000000-0000-4000-8000-000000000001";
    private const string RepairId = "f1000000-0000-4000-8000-000000000002";
    private const string Model = "test-model";
    private const string PromptVersion = "test-v1";

    private static readonly IReadOnlyList<string> Models = [Model];

    [Fact]
    public async Task A_clean_answer_parses_and_lints_and_is_not_repaired()
    {
        var (stage, client, _) = Stage(nameof(A_clean_answer_parses_and_lints_and_is_not_repaired));

        var readings = await stage.Run([CleanCard()], Models, PromptVersion, CancellationToken.None);

        var reading = Assert.Single(readings);
        Assert.Null(reading.ParseError);
        Assert.NotNull(reading.Definition);
        Assert.Equal("Thornwick Insight", reading.Definition!.Name);
        Assert.False(reading.Lint!.HasErrors);
        Assert.True(reading.Ok);
        Assert.False(reading.Repaired);
        Assert.Empty(client.Repairs);
    }

    [Fact]
    public async Task Every_request_carries_the_cached_vocabulary_and_the_card()
    {
        var (stage, client, _) = Stage(nameof(Every_request_carries_the_cached_vocabulary_and_the_card));

        await stage.Run([CleanCard()], Models, PromptVersion, CancellationToken.None);

        var request = Assert.Single(Assert.Single(client.Submissions).Requests);
        Assert.Equal(CleanId, request.CustomId);
        Assert.Contains("## Effect kinds", request.System, StringComparison.Ordinal);
        Assert.Contains("Thornwick Insight", request.User, StringComparison.Ordinal);
        Assert.Contains(CleanId, request.User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reading_the_linter_rejects_is_repaired_once()
    {
        var (stage, client, _) = Stage(nameof(A_reading_the_linter_rejects_is_repaired_once));

        var readings = await stage.Run([RepairCard()], Models, PromptVersion, CancellationToken.None);

        var reading = Assert.Single(readings);
        Assert.True(reading.Repaired);
        Assert.True(reading.Ok);
        var repair = Assert.Single(client.Repairs);
        Assert.Contains("L001", repair.Problem, StringComparison.Ordinal);
        Assert.Contains("mood of a swamp", repair.PreviousAnswer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reading_the_repair_replaced_failed_the_linter()
    {
        var (stage, _, store) = Stage(nameof(The_reading_the_repair_replaced_failed_the_linter));

        await stage.Run([RepairCard()], Models, PromptVersion, CancellationToken.None);

        var first = store.Reading(RepairId, Model, PromptVersion);
        Assert.NotNull(first);
        var (definition, error) = TranslationPrompt.Parse(first!.Text);
        Assert.Null(error);
        var lint = Linter.Check(FileOf(definition!));
        Assert.True(lint.HasErrors);
        Assert.Contains(lint.Findings, f => f.Code == "L001");
    }

    [Fact]
    public async Task A_second_run_submits_nothing_and_repairs_nothing()
    {
        var (stage, client, _) = Stage(nameof(A_second_run_submits_nothing_and_repairs_nothing));
        IReadOnlyList<CorpusCard> cards = [CleanCard(), RepairCard()];

        var first = await stage.Run(cards, Models, PromptVersion, CancellationToken.None);
        var submitted = client.Submissions.Count;
        var repaired = client.Repairs.Count;
        var second = await stage.Run(cards, Models, PromptVersion, CancellationToken.None);

        Assert.Equal(2, first.Count);
        Assert.All(first, r => Assert.True(r.Ok));
        Assert.Equal(1, submitted);
        Assert.Equal(1, repaired);
        Assert.Equal(submitted, client.Submissions.Count);
        Assert.Equal(repaired, client.Repairs.Count);
        Assert.All(second, r => Assert.True(r.Ok));
    }

    [Fact]
    public async Task A_prompt_version_change_is_a_new_reading()
    {
        var (stage, client, _) = Stage(nameof(A_prompt_version_change_is_a_new_reading));

        await stage.Run([CleanCard()], Models, PromptVersion, CancellationToken.None);
        await stage.Run([CleanCard()], Models, "test-v2", CancellationToken.None);

        Assert.Equal(2, client.Submissions.Count);
    }

    [Fact]
    public async Task A_stored_batch_that_was_never_collected_is_collected_not_resubmitted()
    {
        var (stage, client, store) = Stage(nameof(A_stored_batch_that_was_never_collected_is_collected_not_resubmitted));
        store.WriteBatch(new StoredBatch("batch-resumed", Model, PromptVersion, [CleanId, RepairId], Collected: false));
        client.PrepareBatch("batch-resumed", CleanId, RepairId);

        var readings = await stage.Run([CleanCard(), RepairCard()], Models, PromptVersion, CancellationToken.None);

        Assert.Empty(client.Submissions);
        Assert.All(readings, r => Assert.True(r.Ok));
        Assert.Equal("batch-resumed", readings[0].BatchId);
        Assert.True(store.Batch("batch-resumed")!.Collected);
    }

    [Fact]
    public async Task A_collected_batch_is_recorded_before_it_is_polled()
    {
        var (stage, client, store) = Stage(nameof(A_collected_batch_is_recorded_before_it_is_polled));
        client.PollsBeforeEnd = 2;

        await stage.Run([CleanCard()], Models, PromptVersion, CancellationToken.None);

        var batch = Assert.Single(store.Batches());
        Assert.Equal(Model, batch.Model);
        Assert.True(batch.Collected);
        Assert.Equal(CleanId, Assert.Single(batch.OracleIds));
    }

    [Fact]
    public async Task The_raw_answer_is_stored_even_when_it_does_not_parse()
    {
        var (stage, client, store) = Stage(nameof(The_raw_answer_is_stored_even_when_it_does_not_parse), repair: false);
        client.Answer(RepairId, Fixture("glimmerfen-rite.garbage.txt"));

        var readings = await stage.Run([RepairCard()], Models, PromptVersion, CancellationToken.None);

        var reading = Assert.Single(readings);
        Assert.Null(reading.Definition);
        Assert.NotNull(reading.ParseError);
        Assert.True(File.Exists(store.ReadingPath(RepairId, Model, PromptVersion)));
        var stored = store.Reading(RepairId, Model, PromptVersion);
        Assert.Equal(Fixture("glimmerfen-rite.garbage.txt"), stored!.Text);
        Assert.Equal("batch-1", stored.BatchId);
        Assert.Equal(9000, stored.CacheReadTokens);
    }

    [Fact]
    public async Task A_repair_is_stored_beside_the_raw_answer_it_replaced()
    {
        var (stage, _, store) = Stage(nameof(A_repair_is_stored_beside_the_raw_answer_it_replaced));

        await stage.Run([RepairCard()], Models, PromptVersion, CancellationToken.None);

        Assert.Contains("Unsupported", store.Reading(RepairId, Model, PromptVersion)!.Text, StringComparison.Ordinal);
        var repair = store.RepairReading(RepairId, Model, PromptVersion);
        Assert.NotNull(repair);
        Assert.True(repair!.Repaired);
        Assert.DoesNotContain("Unsupported", repair.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_are_split_into_batches_of_at_most_the_batch_size()
    {
        var (stage, client, _) = Stage(nameof(Requests_are_split_into_batches_of_at_most_the_batch_size), batchSize: 1);

        await stage.Run([CleanCard(), RepairCard()], Models, PromptVersion, CancellationToken.None);

        Assert.Equal(2, client.Submissions.Count);
        Assert.All(client.Submissions, b => Assert.Single(b.Requests));
    }

    [Fact]
    public async Task A_batch_size_over_the_api_limit_is_clamped_to_it()
    {
        var (stage, client, _) = Stage(nameof(A_batch_size_over_the_api_limit_is_clamped_to_it), batchSize: 50_000);

        await stage.Run([CleanCard(), RepairCard()], Models, PromptVersion, CancellationToken.None);

        Assert.Equal(2, Assert.Single(client.Submissions).Requests.Count);
    }

    [Fact]
    public async Task A_repair_the_transport_cannot_make_leaves_the_reading_unrepaired_and_is_retried_later()
    {
        // An exhausted credit balance turned a collection of 200 finished batches into a crash;
        // now the batch's results are judged as they stand and the repair waits for a run that can.
        var (stage, client, store) = Stage(nameof(A_repair_the_transport_cannot_make_leaves_the_reading_unrepaired_and_is_retried_later));
        client.RepairsFail = true;

        var readings = await stage.Run([RepairCard()], Models, PromptVersion, CancellationToken.None);

        var reading = Assert.Single(readings);
        Assert.False(reading.Repaired);
        Assert.False(reading.Ok);
        Assert.Single(client.Repairs);
        Assert.Null(store.RepairReading(RepairId, Model, PromptVersion));

        client.RepairsFail = false;
        var again = await stage.Run([RepairCard()], Models, PromptVersion, CancellationToken.None);

        Assert.True(Assert.Single(again).Repaired);
        Assert.Equal(2, client.Repairs.Count);
    }

    [Fact]
    public async Task A_missing_answer_is_a_reading_with_the_error_and_no_definition()
    {
        var (stage, client, _) = Stage(nameof(A_missing_answer_is_a_reading_with_the_error_and_no_definition), repair: false);
        client.Answer(CleanId, string.Empty);

        var readings = await stage.Run([CleanCard()], Models, PromptVersion, CancellationToken.None);

        var reading = Assert.Single(readings);
        Assert.Null(reading.Definition);
        Assert.NotNull(reading.ParseError);
        Assert.False(reading.Ok);
    }

    private static (TranslateStage Stage, FakeTranslationClient Client, TranslationStore Store) Stage(
        string name,
        bool repair = true,
        int batchSize = TranslateStage.MaxBatchSize)
    {
        var client = new FakeTranslationClient();
        client.Answer(CleanId, Fixture("thornwick-insight.clean.txt"));
        client.Answer(RepairId, Fixture("glimmerfen-rite.unsupported.txt"));
        client.RepairWith(RepairId, Fixture("glimmerfen-rite.repaired.txt"));
        var store = new TranslationStore(FreshDirectory(name));
        var options = new TranslateStageOptions(TimeSpan.Zero, batchSize, repair);
        return (new TranslateStage(client, store, options), client, store);
    }

    /// <summary>Test stores live under the worktree's own agent scratch, never /tmp, which is shared across agents.</summary>
    private static string FreshDirectory(string name)
    {
        var path = Repo.Path(".agent", "tmp", "translate-stage-tests", name);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Repo.Path("tests", "MtgEngine.Pipeline.Tests", "Fixtures", "Translate", name));

    /// <summary>An invented instant: one clean line, so a clean answer has something to be clean about.</summary>
    private static CorpusCard CleanCard() => new(
        CleanId,
        "Thornwick Insight",
        "normal",
        [new CorpusFace("Thornwick Insight", "Instant", "{1}{U}", "Draw two cards.", null, null, null, null)],
        [],
        []);

    /// <summary>An invented sorcery whose second line has no vocabulary record, so the first reading lints L001.</summary>
    private static CorpusCard RepairCard() => new(
        RepairId,
        "Glimmerfen Rite",
        "normal",
        [new CorpusFace("Glimmerfen Rite", "Sorcery", "{1}{B}", "Each opponent loses 2 life. You gain life equal to the mood of the swamp.", null, null, null, null)],
        [],
        []);

    private static CardFile FileOf(CardDefinition definition) => new(
        RepairId,
        "Glimmerfen Rite",
        RepairCard().OracleHash,
        IrVersion.Current,
        definition,
        ImplementationStatus.Translated,
        new TranslatedProvenance([Model], PromptVersion, Agreed: false));
}
