using MtgEngine.Cards.Pipeline.Verify;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// <c>pipeline status</c>: where a directory of card files stands, read off the files. Every card
/// here is invented and written into a scratch directory; the command plays nothing, so the test
/// proves the files are still byte for byte what it found.
/// </summary>
public sealed class VerifyCommandStatusTests : IDisposable
{
    private readonly string _cards = Path.Combine(AppContext.BaseDirectory, "StatusCommandTests", Guid.NewGuid().ToString("n"), "cards");

    public VerifyCommandStatusTests() => Directory.CreateDirectory(_cards);

    [Fact]
    public void Status_counts_the_directory_the_way_a_verification_run_reports_it()
    {
        Write(Verified("a1"));
        Write(Verified("a2"));
        Write(Failed("b1"));
        Write(NoScenarios("c1"));
        Write(NotRun("d1"));
        Write(Marked("e1", "a shared land type"));
        Write(Disputed("f1"));

        var log = new StringWriter();
        var before = Snapshot();

        var code = StatusCommand.Run(_cards, log);

        Assert.Equal(0, code);
        var summary = StatusCommand.Of(CardDirectory.Load(_cards));
        Assert.Equal(7, summary.Files);
        Assert.Equal(2, summary.Verified);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.NoScenarios);
        Assert.Equal(1, summary.Unsupported);
        Assert.Equal(1, summary.NotRun);
        Assert.Equal(1, summary.Disputed);
        Assert.Equal("engine-test", summary.EngineHash);
        Assert.Contains("2 verified, 1 failed, 1 with no scenarios, 1 unsupported, 1 not run", log.ToString(), StringComparison.Ordinal);
        Assert.Contains("1 disputed", log.ToString(), StringComparison.Ordinal);
        // Read, not played: no game ran and no file was rewritten.
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Status_names_what_the_unsupported_cards_are_waiting_for()
    {
        Write(Marked("e1", "a shared land type"));
        Write(Marked("e2", "a shared land type"));
        Write(Marked("e3", "gift"));

        var log = new StringWriter();

        StatusCommand.Run(_cards, log);

        var summary = StatusCommand.Of(CardDirectory.Load(_cards));
        Assert.Equal(3, summary.Unsupported);
        Assert.Equal(2, summary.UnsupportedReasons.Count);
        Assert.Equal("a shared land type", summary.UnsupportedReasons[0].Reason);
        Assert.Equal(2, summary.UnsupportedReasons[0].Count);
        Assert.Contains("unsupported x2: a shared land type", log.ToString(), StringComparison.Ordinal);
        Assert.Contains("unsupported x1: gift", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_directory_with_no_card_files_says_so_rather_than_reporting_zeroes_as_a_state()
    {
        var log = new StringWriter();

        var code = StatusCommand.Run(_cards, log);

        Assert.Equal(StatusCommand.NothingToDo, code);
        Assert.Contains("of 0 files", log.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_cards)!;
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    /// <summary>Every file's name and content, so a command that claims to read can be held to it.</summary>
    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(_cards, "*.json").ToDictionary(p => Path.GetFileName(p), p => File.ReadAllText(p), StringComparer.Ordinal);

    private void Write(CardFile file) =>
        File.WriteAllText(Path.Combine(_cards, file.OracleId + ".json"), CardFileSerializer.Write(file));

    private static CardFile Verified(string oracleId) => WithScenario(Card(oracleId, new SpellAbility(new Draw(new Fixed(1))))) with
    {
        Status = ImplementationStatus.Verified,
        Verification = new Verification("engine-test", "verify-test", 1, 1, DateTimeOffset.UnixEpoch),
    };

    /// <summary>A card the engine played and refuted: the run demoted it and left the counts behind.</summary>
    private static CardFile Failed(string oracleId) => WithScenario(Card(oracleId, new SpellAbility(new Draw(new Fixed(1))))) with
    {
        Verification = new Verification("engine-test", "verify-test", 0, 1, DateTimeOffset.UnixEpoch),
    };

    /// <summary>A card with a scenario and no verification: nothing has played it yet.</summary>
    private static CardFile NotRun(string oracleId) => WithScenario(Card(oracleId, new SpellAbility(new Draw(new Fixed(1)))));

    /// <summary>A card nothing can assert, so no run ever gave it a scenario.</summary>
    private static CardFile NoScenarios(string oracleId) => Card(oracleId, new SpellAbility(new Draw(new Fixed(1))));

    private static CardFile Marked(string oracleId, string reason) => Card(oracleId, new SpellAbility(new Unsupported(reason)));

    private static CardFile Disputed(string oracleId) => Card(oracleId, new SpellAbility(new Draw(new Fixed(1)))) with
    {
        Status = ImplementationStatus.Disputed,
        Source = new TranslatedProvenance(["model-a", "model-b"], "test-v1", Agreed: false),
    };

    private static CardFile WithScenario(CardFile file) => file with
    {
        Scenarios = [new Scenario(
            "smoke-" + file.OracleId,
            file.Name,
            new GeneratedSource("test"),
            new ScenarioSetup([new PlayerSetup("A"), new PlayerSetup("B")], "A"),
            [],
            [new Life("A", 20)])],
    };

    private static CardFile Card(string oracleId, Ability ability) => new(
        oracleId,
        "Status Card " + oracleId,
        OracleHashing.Of(["Status Card " + oracleId]),
        IrVersion.Current,
        new CardDefinition(
            oracleId,
            "Status Card " + oracleId,
            Layout.Normal,
            [new Face("Status Card " + oracleId, FaceRole.Main, new TypeLine(CardType.Instant), ManaCost: "{1}", Abilities: [ability])]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));
}
