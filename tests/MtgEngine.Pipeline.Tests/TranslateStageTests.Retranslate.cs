using MtgEngine.Cards.Pipeline.Translate;
using MtgEngine.Cards.Pipeline.Verify;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// The re-translation round chosen off <c>cards/</c> and rehearsed against the fake transport:
/// which faces a run would submit, and — the point of the test — which it would not.
/// </summary>
/// <remarks>
/// The round is credit-bound (about $19 for 200 faces, CARD_ENGINE_PLAN.md § "Decision points"),
/// so resubmitting a verified card is money spent to be told what the file already says. Every
/// card here is invented and written into a scratch directory; nothing reaches the network.
/// </remarks>
public sealed class TranslateStageRetranslateTests : IDisposable
{
    private const string DisputedOne = "00000000-0000-0000-0000-0000000000d1";
    private const string DisputedTwo = "00000000-0000-0000-0000-0000000000d2";
    private const string Marked = "00000000-0000-0000-0000-0000000000u1";
    private const string VerifiedOne = "00000000-0000-0000-0000-0000000000v1";
    private const string VerifiedTwo = "00000000-0000-0000-0000-0000000000v2";
    private const string Dropped = "00000000-0000-0000-0000-0000000000x1";
    private const string Model = "test-model";

    private readonly string _cards = Path.Combine(AppContext.BaseDirectory, "RetranslateTests", Guid.NewGuid().ToString("n"), "cards");

    public TranslateStageRetranslateTests()
    {
        Directory.CreateDirectory(_cards);
        Write(Disputed(DisputedOne, "Retranslate Envoy"));
        Write(Disputed(DisputedTwo, "Retranslate Herald"));
        Write(MarkedCard(Marked));
        Write(Verified(VerifiedOne, "Retranslate Scribe"));
        Write(Verified(VerifiedTwo, "Retranslate Warden"));
        Write(Verified(Dropped, "Retranslate Relic") with { Status = ImplementationStatus.Excluded, Source = new ExcludedProvenance("test") });
    }

    [Fact]
    public void The_round_takes_the_disputed_and_the_unsupported_and_no_verified_card()
    {
        Assert.True(RetranslateSelection.TryParse(RetranslateSelection.DefaultSpec, out var kinds, out var error), error);

        var selection = RetranslateSelection.Select(CardDirectory.Load(_cards), kinds);

        Assert.Equal<string>([DisputedOne, DisputedTwo, Marked], [.. selection.Select(i => i.OracleId).Order(StringComparer.Ordinal)]);
        Assert.DoesNotContain(VerifiedOne, selection.Select(i => i.OracleId), StringComparer.Ordinal);
        Assert.DoesNotContain(VerifiedTwo, selection.Select(i => i.OracleId), StringComparer.Ordinal);
        Assert.DoesNotContain(Dropped, selection.Select(i => i.OracleId), StringComparer.Ordinal);
        Assert.Contains("disputed", selection.Single(i => i.OracleId == DisputedOne).Why, StringComparison.Ordinal);
        Assert.Contains("no record for this", selection.Single(i => i.OracleId == Marked).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_word_that_is_not_a_kind_is_refused_rather_than_selecting_nothing()
    {
        Assert.False(RetranslateSelection.TryParse("disputed,verified", out _, out var error));
        Assert.Contains("verified", error!, StringComparison.Ordinal);
        Assert.False(RetranslateSelection.TryParse(string.Empty, out _, out _));
    }

    [Fact]
    public async Task Only_the_selected_faces_are_submitted_with_the_current_prompt()
    {
        RetranslateSelection.TryParse(null, out var kinds, out _);
        var selection = RetranslateSelection.Select(CardDirectory.Load(_cards), kinds);
        var batch = RetranslateSelection.Cards(Corpus(), selection);
        var client = new FakeTranslationClient();
        var store = new TranslationStore(Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(_cards)!, "translate")).FullName);
        var stage = new TranslateStage(client, store, new TranslateStageOptions(TimeSpan.Zero, Repair: false));
        var version = TranslationPrompt.VersionOf("retranslate");

        var readings = await stage.Run(batch.Cards, [Model], version, CancellationToken.None);

        Assert.Empty(batch.Missing);
        var submitted = Assert.Single(client.Submissions);
        Assert.Equal<string>([DisputedOne, DisputedTwo, Marked], [.. submitted.Requests.Select(r => r.CustomId).Order(StringComparer.Ordinal)]);
        Assert.DoesNotContain(VerifiedOne, submitted.Requests.Select(r => r.CustomId), StringComparer.Ordinal);
        Assert.All(readings, r => Assert.Equal(version, r.PromptVersion));
        // The current prompt: the later records' spellings and the generated vocabulary, both.
        Assert.Contains("One spelling, continued", submitted.Requests[0].System, StringComparison.Ordinal);
        Assert.Contains("## Effect kinds", submitted.Requests[0].System, StringComparison.Ordinal);
    }

    [Fact]
    public void A_selected_face_the_corpus_does_not_cover_is_reported_rather_than_dropped()
    {
        RetranslateSelection.TryParse(null, out var kinds, out _);
        var selection = RetranslateSelection.Select(CardDirectory.Load(_cards), kinds);

        var batch = RetranslateSelection.Cards(Corpus().Where(c => c.OracleId != Marked), selection);

        Assert.Equal(2, batch.Cards.Count);
        Assert.Equal(Marked, Assert.Single(batch.Missing));
        var log = new StringWriter();
        RetranslateSelection.Print(selection, log);
        Assert.Contains("3 faces would be submitted", log.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_cards)!;
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static IEnumerable<CorpusCard> Corpus() =>
        new[] { DisputedOne, DisputedTwo, Marked, VerifiedOne, VerifiedTwo }
            .Select(id => new CorpusCard(
                id,
                "Retranslate Face " + id[^2..],
                "normal",
                [new CorpusFace("Retranslate Face " + id[^2..], "Instant", "{1}", "Draw a card.", null, null, null, null)],
                [],
                []));

    private void Write(CardFile file) =>
        File.WriteAllText(Path.Combine(_cards, file.OracleId + ".json"), CardFileSerializer.Write(file));

    private static CardFile Disputed(string oracleId, string name) =>
        Card(oracleId, name, new SpellAbility(new Draw(new Fixed(1)))) with
        {
            Status = ImplementationStatus.Disputed,
            Source = new TranslatedProvenance(["model-a", "model-b"], "test-v1", Agreed: false),
        };

    /// <summary>A card whose definition says, in the vocabulary's own words, that part of it is not written.</summary>
    private static CardFile MarkedCard(string oracleId) =>
        Card(oracleId, "Retranslate Cipher", new SpellAbility(new Unsupported("no record for this")));

    private static CardFile Verified(string oracleId, string name)
    {
        var file = Card(oracleId, name, new SpellAbility(new Draw(new Fixed(1))));
        return file with
        {
            Status = ImplementationStatus.Verified,
            Verification = new Verification("engine-test", "verify-test", 1, 1, DateTimeOffset.UnixEpoch),
            Scenarios = [new Scenario("smoke-" + oracleId, name, new GeneratedSource("test"), new ScenarioSetup([new PlayerSetup("A"), new PlayerSetup("B")], "A"), [], [new Life("A", 20)])],
        };
    }

    private static CardFile Card(string oracleId, string name, Ability ability) => new(
        oracleId,
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition(
            oracleId,
            name,
            Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(CardType.Instant), ManaCost: "{1}", Abilities: [ability])]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));
}
