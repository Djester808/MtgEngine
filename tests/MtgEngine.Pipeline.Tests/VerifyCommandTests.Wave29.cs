using MtgEngine.Cards.Pipeline.Agree;
using MtgEngine.Cards.Pipeline.Verify;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// Wave 29 (E3q § 2): a verify run that fails a card whose file already reads Verified cannot write
/// its verdict — <see cref="CardFileWriter"/> refuses a write that lowers a Verified file whose
/// oracle text has not changed, the rule that stops a re-translation undoing a verification. The run
/// therefore plays one set of scenarios while the file keeps another and its old pass, which is how
/// a card can be reported failing and read "Verified 1/1" on disk in the same wave. Until the writer
/// can tell a verification from a re-translation the run says so out loud.
/// </summary>
public sealed class VerifyCommandTestsWave29 : IDisposable
{
    private const string Kept = "00000000-0000-0000-0000-0000000000f1";
    private const string Fresh = "00000000-0000-0000-0000-0000000000f2";

    private readonly string _cards = Path.Combine(AppContext.BaseDirectory, "VerifyCommandTestsWave29", Guid.NewGuid().ToString("n"), "cards");

    public VerifyCommandTestsWave29() => Directory.CreateDirectory(_cards);

    /// <summary>
    /// The card on disk is Verified from an older engine. This run plays the scenarios the generator
    /// makes today, they fail, and the demotion the stage decided never lands. The failure is real
    /// and so is the file's claim, and the two disagree — so the run counts it and names it, rather
    /// than reporting a failure whose only trace is the log of a run nobody kept.
    /// </summary>
    [Fact]
    [RuleRef("602.2")]
    public void A_failing_run_over_a_verified_file_is_reported_as_stale()
    {
        Write(Card(Kept, "Wave29 Ledger") with
        {
            Status = ImplementationStatus.Verified,
            Verification = new Verification("engine:0000000000000000", "verify-earlier", 1, 1, DateTimeOffset.UnixEpoch),
        });
        var log = new StringWriter();

        var summary = VerifyCommand.Verify(
            CardDirectory.Load(_cards), new CardFileWriter(_cards), only: null, log, _ => new VerifyCommandTestsWave29FailingRunner());

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.Failed);
        Assert.Equal(1, summary.Stale);
        Assert.Contains("STALE FILE", log.ToString(), StringComparison.Ordinal);

        // The disagreement the count is about: the file is untouched, still Verified, still carrying
        // the older run's engine hash and its 1/1.
        var onDisk = new CardFileWriter(_cards).Load(Kept);
        Assert.NotNull(onDisk);
        Assert.Equal(ImplementationStatus.Verified, onDisk!.Status);
        Assert.Equal("verify-earlier", onDisk.Verification?.RunId);
    }

    /// <summary>
    /// The falsifier: the same failing run over a Translated file writes, so its verdict and its
    /// scenarios are on disk and nothing is stale. The count is about the writer's refusal and not
    /// about failing.
    /// </summary>
    [Fact]
    [RuleRef("602.2")]
    public void A_failing_run_over_a_translated_file_writes_and_is_not_stale()
    {
        Write(Card(Fresh, "Wave29 Tally"));
        var log = new StringWriter();

        var summary = VerifyCommand.Verify(
            CardDirectory.Load(_cards), new CardFileWriter(_cards), only: null, log, _ => new VerifyCommandTestsWave29FailingRunner());

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.Failed);
        Assert.Equal(0, summary.Stale);
        Assert.DoesNotContain("STALE FILE", log.ToString(), StringComparison.Ordinal);

        var onDisk = new CardFileWriter(_cards).Load(Fresh);
        Assert.NotNull(onDisk);
        Assert.Equal(0, onDisk!.Verification?.Passed);
        Assert.NotEmpty(onDisk.ScenarioList);
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_cards);
        if (root is not null && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private void Write(CardFile file) =>
        File.WriteAllText(Path.Combine(_cards, file.OracleId + ".json"), CardFileSerializer.Write(file));

    private static CardFile Card(string oracleId, string name) => new(
        oracleId,
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition(
            oracleId,
            name,
            Layout.Normal,
            [
                new Face(
                    name,
                    FaceRole.Main,
                    new TypeLine(CardType.Artifact),
                    ManaCost: "{1}",
                    Abilities: [new ActivatedAbility([new ManaCost("{1}"), new TapSelf()], new GainLife(new Fixed(3)))]),
            ]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));

    /// <summary>A runner that fails every scenario it is given, so the verdict is the stage's to write.</summary>
    private sealed class VerifyCommandTestsWave29FailingRunner : IScenarioRunner
    {
        public ScenarioResult Run(Scenario scenario, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            return new ScenarioResult(
                scenario.Id,
                false,
                [.. scenario.Expect.Select(e => new ExpectationResult(e, false, "not met"))]);
        }
    }
}
