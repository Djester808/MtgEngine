using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// Wave 15 (P17): two of three readings agree, a third batch that names only some of the cards,
/// a one-model submit recorded beside the others, and the corpus slices. Fake client, scratch
/// store, made-up cards; nothing reaches the corpus or the network.
/// </summary>
public sealed partial class PilotTests
{
    private const string SeerId = "00000000-0000-0000-0000-0000000000a3";

    private static CardDefinition DrawingTwo(string oracleId, string name) => Definition(oracleId, name, new Draw(new Fixed(2)));

    [Fact]
    public async Task Three_readings_agree_on_any_two_and_the_odd_one_is_reported_not_kept()
    {
        var harness = new Harness([Opus, Sonnet, Fable]);
        harness.Client.Answer(Opus, ScribeId, Json(Drawing(ScribeId, "Test Scribe")));
        harness.Client.Answer(Sonnet, ScribeId, Json(Milling(ScribeId, "Test Scribe")));
        harness.Client.Answer(Fable, ScribeId, Json(Drawing(ScribeId, "Test Scribe")));
        harness.Client.Answer(Opus, SageId, Json(Drawing(SageId, "Test Sage")));
        harness.Client.Answer(Sonnet, SageId, Json(Milling(SageId, "Test Sage")));
        harness.Client.Answer(Fable, SageId, Json(DrawingTwo(SageId, "Test Sage")));
        await Pilot.SubmitAsync(harness.Environment, harness.Cards, harness.OutDir, CancellationToken.None);
        Assert.Equal(3, harness.Client.Submissions.Count);

        var code = await Pilot.CollectAsync(harness.Environment, harness.OutDir, CancellationToken.None);

        Assert.Equal(0, code);

        // Opus and fable said the same thing: agreed, provenance names the two, sonnet is not a candidate.
        var agreed = harness.Writer.Load(ScribeId);
        Assert.NotNull(agreed);
        Assert.Equal(ImplementationStatus.Translated, agreed.Status);
        var provenance = Assert.IsType<TranslatedProvenance>(agreed.Source);
        Assert.True(provenance.Agreed);
        Assert.Equal(new[] { Opus, Fable }, provenance.Models.ToArray());
        Assert.True(agreed.Candidates is null || agreed.Candidates.Count == 0);

        // No two of three agree: disputed, every reading a candidate.
        var disputed = harness.Writer.Load(SageId);
        Assert.NotNull(disputed);
        Assert.Equal(ImplementationStatus.Disputed, disputed.Status);
        Assert.Equal(3, disputed.Candidates?.Count);

        var report = await File.ReadAllTextAsync(Path.Combine(harness.OutDir, "report.md"), CancellationToken.None);
        Assert.Contains("## " + Fable, report, StringComparison.Ordinal);
        Assert.Contains("  agreed by pair: " + Opus + "+" + Fable + ": 1", report, StringComparison.Ordinal);
        Assert.Contains("## Outvoted readings (1 cards)", report, StringComparison.Ordinal);
        Assert.Contains("- Test Scribe: " + Sonnet + " differs at ", report, StringComparison.Ordinal);
        Assert.Contains("- Test Sage: " + Opus + ": clean | " + Sonnet + ": clean | " + Fable + ": clean", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_third_batch_naming_a_subset_judges_the_rest_on_two_and_again_reads_all_three()
    {
        var harness = new Harness();
        harness.Client.Answer(Opus, ScribeId, Json(Drawing(ScribeId, "Test Scribe")));
        harness.Client.Answer(Sonnet, ScribeId, Json(Drawing(ScribeId, "Test Scribe")));
        harness.Client.Answer(Opus, SageId, Json(Drawing(SageId, "Test Sage")));
        harness.Client.Answer(Sonnet, SageId, Json(Milling(SageId, "Test Sage")));
        await Pilot.SubmitAsync(harness.Environment, harness.Cards, harness.OutDir, CancellationToken.None);

        // A third translator reads only the disputed card, for one model, recorded beside the other two.
        harness.Client.Answer(Fable, SageId, Json(Milling(SageId, "Test Sage")));
        var code = await Pilot.SubmitAsync(harness.Environment, [harness.Cards[1]], harness.OutDir, CancellationToken.None, models: [Fable], merge: true);
        Assert.Equal(0, code);
        var submission = PilotSubmission.Read(harness.OutDir);
        Assert.NotNull(submission);
        Assert.Equal(new[] { Opus, Sonnet, Fable }, submission.Batches.Keys.ToArray());
        Assert.Equal(new[] { ScribeId, SageId }, submission.OracleIds.ToArray());

        // A batch the store never heard of — one written by hand — is recorded with the ids its
        // own batch file names, not the whole submission's.
        var fableBatch = submission.Batches[Fable];
        File.Delete(harness.Store.BatchPath(fableBatch));

        code = await Pilot.CollectAsync(harness.Environment, harness.OutDir, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(new[] { SageId }, harness.Store.Batch(fableBatch)!.OracleIds.ToArray());
        // The stage submitted nothing new: the card the third batch does not name was not sent to fable.
        Assert.Equal(3, harness.Client.Submissions.Count);

        var scribe = harness.Writer.Load(ScribeId);
        Assert.NotNull(scribe);
        Assert.Equal(new[] { Opus, Sonnet }, Assert.IsType<TranslatedProvenance>(scribe.Source).Models.ToArray());

        var sage = harness.Writer.Load(SageId);
        Assert.NotNull(sage);
        Assert.Equal(ImplementationStatus.Translated, sage.Status);
        Assert.Equal(new[] { Sonnet, Fable }, Assert.IsType<TranslatedProvenance>(sage.Source).Models.ToArray());

        var report = await File.ReadAllTextAsync(Path.Combine(harness.OutDir, "report.md"), CancellationToken.None);
        Assert.Contains("  agreed by pair: " + Opus + "+" + Sonnet + ": 1, " + Sonnet + "+" + Fable + ": 1", report, StringComparison.Ordinal);

        // --again with three batches: the third's one answer is read again too.
        harness.Client.Answer(Fable, SageId, Json(Drawing(SageId, "Test Sage")));
        code = await Pilot.CollectAsync(harness.Environment, harness.OutDir, CancellationToken.None, again: true);

        Assert.Equal(0, code);
        Assert.Contains(Fable + " " + fableBatch + ": reading its 1 answers again", harness.Log.ToString(), StringComparison.Ordinal);
        Assert.Equal(new[] { Opus, Fable }, Assert.IsType<TranslatedProvenance>(harness.Writer.Load(SageId)!.Source).Models.ToArray());
    }

    [Fact]
    public async Task A_card_named_by_one_batch_only_is_skipped_not_judged()
    {
        var harness = new Harness();
        harness.Client.Answer(Fable, ScribeId, Json(Drawing(ScribeId, "Test Scribe")));

        await Pilot.SubmitAsync(harness.Environment, [harness.Cards[0]], harness.OutDir, CancellationToken.None, models: [Fable]);
        var code = await Pilot.CollectAsync(harness.Environment, harness.OutDir, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Null(harness.Writer.Load(ScribeId));
        Assert.Contains("Test Scribe: named by 1 batch(es); agreement needs two, skipped.", harness.Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_next_slice_skips_filed_and_submitted_faces_and_scopes_its_batch_ids()
    {
        var harness = new Harness();
        var seer = Harness.Card(SeerId, "Test Seer");
        var candidates = new[] { harness.Cards[0], harness.Cards[1], seer };

        // Scribe has a card file, Sage was submitted, Seer is what comes next.
        harness.Writer.Write(new CardFile(
            ScribeId,
            "Test Scribe",
            harness.Cards[0].OracleHash,
            IrVersion.Current,
            Drawing(ScribeId, "Test Scribe"),
            ImplementationStatus.Translated,
            new TranslatedProvenance([Opus, Sonnet], Harness.PromptVersion, Agreed: true)));
        await Pilot.SubmitAsync(harness.Environment, [harness.Cards[1]], harness.OutDir, CancellationToken.None);

        var done = Pilot.Done(harness.Environment);
        Assert.Equal(new[] { ScribeId, SageId }, done.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { SeerId }, Pilot.Next(candidates, 5, done).Select(c => c.OracleId).ToArray());
        Assert.Empty(Pilot.Next(candidates, 5, new HashSet<string>(candidates.Select(c => c.OracleId), StringComparer.Ordinal)));

        // Slices are numbered from 001, one past the directories already there.
        var slices = Path.Combine(harness.OutDir, "slices");
        Assert.Equal("001", Path.GetFileName(Pilot.NextSliceDirectory(slices)));
        Directory.CreateDirectory(Path.Combine(slices, "001"));
        Assert.Equal("002", Path.GetFileName(Pilot.NextSliceDirectory(slices)));

        // A scoped file client names its batches after the slice, so two slices never share an
        // id in the store; the batch file says which cards it names.
        var client = new FileTranslationClient(Path.Combine(slices, "001", "local"), "001");
        var id = await client.SubmitBatch(Opus, [(SeerId, "system", "user")], CancellationToken.None);
        Assert.Equal("local-001-" + Opus + "-001", id);
        Assert.Equal(new[] { SeerId }, client.Batch(id)!.CustomIds.ToArray());
        Assert.Null(client.Batch("no-such-batch"));

        // Without a scope a second out directory repeats the first's ids, and the shared store refuses it.
        var first = harness.Environment with { Client = new FileTranslationClient(Path.Combine(slices, "a", "local")), Models = [Opus] };
        Assert.Equal(0, await Pilot.SubmitAsync(first, [seer], Path.Combine(slices, "a"), CancellationToken.None));
        var second = harness.Environment with { Client = new FileTranslationClient(Path.Combine(slices, "b", "local")), Models = [Opus] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Pilot.SubmitAsync(second, [seer], Path.Combine(slices, "b"), CancellationToken.None));
    }
}
