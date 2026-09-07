using System.Text.Json;
using MtgEngine.Cards.Pipeline.Agree;
using MtgEngine.Cards.Pipeline.Verify;
using MtgEngine.Rules.Events;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// The verification loop as commands: <c>pipeline verify</c> over a directory of card files, and
/// the two rulings commands that fill that directory with scenarios.
/// </summary>
/// <remarks>
/// Every card here is made up and written into a scratch directory; the verify tests run the real
/// engine, which is the point of them — "compiles" and "deserialises" are not statuses — and the
/// rulings tests run against the fake transport, so nothing reaches the network or reads a key.
/// </remarks>
public sealed class VerifyCommandTests : IDisposable
{
    private const string Scribe = "00000000-0000-0000-0000-0000000000f1";
    private const string Mute = "00000000-0000-0000-0000-0000000000f2";
    private const string Sonnet = "claude-sonnet-5";
    private const string Opus = "claude-opus-5";

    private readonly string _cards = Path.Combine(AppContext.BaseDirectory, "VerifyCommandTests", Guid.NewGuid().ToString("n"), "cards");
    private readonly string _out;

    public VerifyCommandTests()
    {
        _out = Path.Combine(Path.GetDirectoryName(_cards)!, "rulings");
        Directory.CreateDirectory(_cards);
    }

    [Fact]
    public void Verify_plays_the_generated_smoke_scenario_and_records_the_engine_hash()
    {
        Write(Creature(Scribe));
        var log = new StringWriter();

        var code = VerifyCommand.Run(_cards, only: null, log);

        Assert.True(code == 0, log.ToString());
        var written = Read(Scribe);
        Assert.Equal(ImplementationStatus.Verified, written.Status);
        Assert.NotNull(written.Verification);
        Assert.Equal(EventLogSerializer.EngineHash(), written.Verification.EngineHash);
        Assert.Equal(written.Verification.Total, written.Verification.Passed);
        // What was verified is what is stored: the scenario the run generated is in the file.
        var scenario = Assert.Single(written.ScenarioList);
        Assert.IsType<GeneratedSource>(scenario.Source);
        Assert.StartsWith("verify-", written.Verification.RunId, StringComparison.Ordinal);
        Assert.Contains("verified", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_only_touches_the_card_it_was_asked_about()
    {
        Write(Creature(Scribe));
        Write(Creature(Mute, "Second Scribe"));

        var log = new StringWriter();

        var code = VerifyCommand.Run(_cards, only: Scribe, log);

        Assert.True(code == 0, log.ToString());
        Assert.Equal(ImplementationStatus.Verified, Read(Scribe).Status);
        var untouched = Read(Mute);
        Assert.Equal(ImplementationStatus.Translated, untouched.Status);
        Assert.Null(untouched.Verification);
    }

    [Fact]
    public void A_card_whose_definition_asserts_nothing_is_reported_and_left_alone()
    {
        Write(NoScenarios(Mute));
        var log = new StringWriter();

        var code = VerifyCommand.Run(_cards, only: null, log);

        Assert.Equal(0, code);
        var untouched = Read(Mute);
        Assert.Equal(ImplementationStatus.Translated, untouched.Status);
        Assert.Null(untouched.Verification);
        Assert.Empty(untouched.ScenarioList);
        Assert.Contains("no scenarios", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rulings_submit_records_one_batch_id_per_model()
    {
        Write(Creature(Scribe));
        var client = new PilotFakeClient { PollsBeforeEnd = 99 };
        var log = new StringWriter();

        var code = await RulingsCommand.SubmitAsync(Rulings(client, log, Sonnet, Opus), _cards, _out, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(2, client.Submissions.Count);
        var submission = RulingsSubmission.Read(_out);
        Assert.NotNull(submission);
        Assert.Equal(2, submission.Batches.Count);
        Assert.Equal(client.Submissions[0].BatchId, submission.Batches[Sonnet]);
        Assert.Equal(client.Submissions[1].BatchId, submission.Batches[Opus]);
        Assert.Equal(_cards, submission.CardsDirectory);
        Assert.Equal(Scribe, Assert.Single(submission.Rulings).OracleId);
    }

    [Fact]
    public async Task Rulings_collect_writes_nothing_while_a_batch_is_still_running()
    {
        Write(Creature(Scribe));
        var client = new PilotFakeClient { PollsBeforeEnd = 99 };
        var environment = Rulings(client, new StringWriter(), Sonnet);
        await RulingsCommand.SubmitAsync(environment, _cards, _out, CancellationToken.None);

        var code = await RulingsCommand.CollectAsync(environment, _out, CancellationToken.None);

        Assert.Equal(RulingsCommand.NotEnded, code);
        Assert.Empty(Read(Scribe).ScenarioList);
    }

    [Fact]
    public async Task Rulings_collect_stores_the_scenario_the_model_wrote_beside_the_card()
    {
        Write(Creature(Scribe));
        var client = new PilotFakeClient();
        client.Answer(Sonnet, "ruling-0", Answer());
        var environment = Rulings(client, new StringWriter(), Sonnet);
        await RulingsCommand.SubmitAsync(environment, _cards, _out, CancellationToken.None);

        var code = await RulingsCommand.CollectAsync(environment, _out, CancellationToken.None);

        Assert.Equal(0, code);
        var scenario = Assert.Single(Read(Scribe).ScenarioList);
        var source = Assert.IsType<RulingSource>(scenario.Source);
        Assert.Equal(Scribe, source.OracleId);
        Assert.Equal("It draws two cards, not one.", source.Comment);
        // The scenario is a claim, not evidence: verifying it is `pipeline verify`'s job.
        Assert.Equal(ImplementationStatus.Translated, Read(Scribe).Status);
    }

    [Fact]
    public async Task Rulings_submit_says_so_when_the_corpus_is_not_in_the_checkout()
    {
        var log = new StringWriter();
        var environment = Rulings(new PilotFakeClient(), log, Sonnet) with { Rulings = null };

        var code = await RulingsCommand.SubmitAsync(environment, _cards, _out, CancellationToken.None);

        Assert.Equal(RulingsCommand.NoCorpus, code);
        Assert.Contains("corpus is not in this checkout", log.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_cards)!;
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private RulingsEnvironment Rulings(PilotFakeClient client, TextWriter log, params string[] models) => new(
        client,
        new CardFileWriter(_cards),
        () => [new RulingEntry("ruling-0", Scribe, "Verify Scribe", "It draws two cards, not one.", "2020-01-01")],
        models,
        log);

    /// <summary>A scenario as a model would answer with one: JSON only, in the real records' shape.</summary>
    private static string Answer() => JsonSerializer.Serialize(
        new Scenario(
            "written-by-the-model",
            "the scribe draws two",
            new GeneratedSource("test"),
            new ScenarioSetup([new PlayerSetup("A", Library: [new CardRef("Verify Scribe")]), new PlayerSetup("B")], "A"),
            [],
            [new Life("A", 20)]),
        CardFileSerializer.Options);

    private void Write(CardFile file) =>
        File.WriteAllText(Path.Combine(_cards, file.OracleId + ".json"), CardFileSerializer.Write(file));

    private CardFile Read(string oracleId) =>
        CardFileSerializer.Read(File.ReadAllText(Path.Combine(_cards, oracleId + ".json")));

    /// <summary>
    /// A creature: the smoke scenario casts it and asserts it arrived on the battlefield. A card
    /// that draws cannot be smoke-verified yet — see seam-requests/P7.md — because a generated
    /// scenario gives no player a library and a draw from an empty one does nothing (CR 121.4).
    /// </summary>
    private static CardFile Creature(string oracleId, string name = "Verify Scribe") => new(
        oracleId,
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition(
            oracleId,
            name,
            Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(CardType.Creature), ManaCost: "{1}", Power: new PrintedStat("1"), Toughness: new PrintedStat("1"))]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));

    /// <summary>An instant whose only effect is one no smoke scenario can assert, so it gets no scenario at all.</summary>
    private static CardFile NoScenarios(string oracleId) =>
        Card(oracleId, "Verify Mute", [new SpellAbility(new Unsupported("nothing a cast can assert"))]);

    private static CardFile Card(string oracleId, string name, IReadOnlyList<Ability> abilities) => new(
        oracleId,
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition(
            oracleId,
            name,
            Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(CardType.Instant), ManaCost: "{1}", Abilities: abilities)]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));
}
