using MtgEngine.Api.Services;
using MtgEngine.Cards;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The third number the coverage dashboard needs (C3): a card the runner had nothing to play is
/// neither verified nor failed, and counting it as either misreports the run. Sixteen of the
/// eighty-seven pilot cards were in that state at the last verify run, so it is not a rounding error.
/// </summary>
public sealed class CardsImplementationTestsCoverage : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "CardsImplementationTestsCoverage", Guid.NewGuid().ToString("n"));

    public CardsImplementationTestsCoverage() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Coverage_counts_the_cards_the_runner_had_nothing_to_play()
    {
        Write(Card("cover-1") with { Status = ImplementationStatus.Verified, Scenarios = [Scenario("cover-1")], Verification = Run() });
        Write(Card("cover-2") with { Status = ImplementationStatus.Translated });
        Write(Card("cover-3") with { Status = ImplementationStatus.Verified });

        // Untranslated cards are not in a run at all, so they are not counted as a scenario gap.
        Write(Card("cover-4") with { Status = ImplementationStatus.Untranslated });

        var coverage = Service().Coverage();

        Assert.Equal(2, coverage.LastVerification!.WithoutScenarios);
    }

    [Fact]
    public void One_cards_own_verification_carries_no_run_wide_count()
    {
        var file = Write(Card("cover-5") with { Status = ImplementationStatus.Verified, Scenarios = [Scenario("cover-5")], Verification = Run() });

        var dto = Service(file).Implementation(file.OracleId);

        Assert.Equal(0, dto.LastVerification!.WithoutScenarios);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static Verification Run() => new("engine:abc", "run-1", 1, 1, DateTimeOffset.UnixEpoch);

    private static Scenario Scenario(string id) => new(
        id + "-smoke",
        "a scenario",
        new GeneratedSource("test"),
        new ScenarioSetup([new PlayerSetup("A")], "A"),
        [],
        [new Life("A", 20)]);

    private CardImplementationService Service(params CardFile[] known) =>
        new(new CardsImplementationCoveragePool(known), new CardImplementationOptions(_directory));

    private CardFile Write(CardFile file)
    {
        File.WriteAllText(Path.Combine(_directory, file.OracleId + ".json"), CardFileSerializer.Write(file));
        return file;
    }

    private static CardFile Card(string oracleId) => new(
        oracleId,
        oracleId,
        OracleHashing.Of([oracleId]),
        IrVersion.Current,
        new CardDefinition(oracleId, oracleId, Layout.Normal,
            [new Face(oracleId, FaceRole.Main, new TypeLine(CardType.Instant), ManaCost: "{1}")]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));

    private sealed class CardsImplementationCoveragePool(params CardFile[] known) : ICardFilePool
    {
        public int Count => known.Length;

        public CardDefinition? ByName(string name) => known.FirstOrDefault(f => f.Name == name)?.Definition;

        public CardDefinition? ByOracleId(string oracleId) => known.FirstOrDefault(f => f.OracleId == oracleId)?.Definition;

        public CardStatus StatusOf(string oracleId)
        {
            var file = known.FirstOrDefault(f => f.OracleId == oracleId);
            return file is null ? CardStatus.Unknown(oracleId) : new CardStatus(file.OracleId, file.Name, file.Status);
        }
    }
}
