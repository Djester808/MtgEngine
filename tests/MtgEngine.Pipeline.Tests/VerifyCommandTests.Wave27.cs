using MtgEngine.Cards.Pipeline.Agree;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Cards.Pipeline.Verify;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// Wave 27 (P26): an <see cref="Unsupported"/> marker refuses the ability it is on and not the card
/// it is on (CR 602.2). A card is called unsupported only when nothing is left to play. Every card
/// here is made up and written into a scratch directory; the runs play the real engine.
/// </summary>
public sealed class VerifyCommandTestsWave27 : IDisposable
{
    private const string Reliquary = "00000000-0000-0000-0000-0000000000e1";
    private const string Mute = "00000000-0000-0000-0000-0000000000e2";

    private readonly string _cards = Path.Combine(AppContext.BaseDirectory, "VerifyCommandTestsWave27", Guid.NewGuid().ToString("n"), "cards");

    public VerifyCommandTestsWave27() => Directory.CreateDirectory(_cards);

    /// <summary>
    /// CR 602.2: the card's second ability is one nobody could write down; its first is a scenario
    /// the generator has always been able to make. The whole card used to be refused for the second,
    /// which is how Emergence Zone spent seven waves in the unsupported count with a playable
    /// <c>{T}: Add {C}</c> on it (seam-requests/P25.md § 4).
    /// </summary>
    [Fact]
    [RuleRef("602.2")]
    public void A_marker_on_one_ability_leaves_the_card_playable_for_the_others()
    {
        Write(Card(
            Reliquary,
            "Wave27 Reliquary",
            new ActivatedAbility([new ManaCost("{1}"), new TapSelf()], new GainLife(new Fixed(3))),
            new ActivatedAbility([new ManaCost("{2}")], new Unsupported("no record for casting as though it had flash"))));
        var log = new StringWriter();

        var summary = VerifyCommand.Verify(CardDirectory.Load(_cards), new CardFileWriter(_cards), only: null, log);

        Assert.NotNull(summary);
        Assert.Equal(0, summary!.Unsupported);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(1, summary.Verified);
        // The scenario that ran is the clean ability's, and the marked one contributed none.
        var outcome = Assert.Single(summary.Outcomes);
        Assert.Contains(SmokeActivation.IdFor(Reliquary, 0), outcome.Results.Select(r => r.ScenarioId), StringComparer.Ordinal);
        Assert.DoesNotContain(SmokeActivation.IdFor(Reliquary, 1), outcome.Results.Select(r => r.ScenarioId), StringComparer.Ordinal);
    }

    /// <summary>
    /// The other side of it: when every ability carries a marker there is nothing to refuse the card
    /// in place of, so the file is still reported unsupported with the reason the translator wrote.
    /// </summary>
    [Fact]
    [RuleRef("602.2")]
    public void A_card_whose_every_ability_is_marked_is_still_unsupported()
    {
        Write(Card(
            Mute,
            "Wave27 Cipher",
            new ActivatedAbility([new ManaCost("{1}")], new Unsupported("no record for the first clause")),
            new ActivatedAbility([new ManaCost("{2}")], new Unsupported("no record for the second clause"))));
        var log = new StringWriter();

        var summary = VerifyCommand.Verify(CardDirectory.Load(_cards), new CardFileWriter(_cards), only: null, log);

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.Unsupported);
        Assert.Equal(0, summary.Verified);
        Assert.Contains("no record for the first clause", log.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_cards);
        if (root is not null && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private void Write(CardFile file) =>
        File.WriteAllText(Path.Combine(_cards, file.OracleId + ".json"), CardFileSerializer.Write(file));

    private static CardFile Card(string oracleId, string name, params Ability[] abilities) => new(
        oracleId,
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition(
            oracleId,
            name,
            Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(CardType.Artifact), ManaCost: "{1}", Abilities: abilities)]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));
}
