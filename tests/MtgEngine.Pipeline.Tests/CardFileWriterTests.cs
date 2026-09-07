using MtgEngine.Cards.Pipeline.Agree;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// The writer's one refusal and its limit. A Verified file is kept over a re-translation of the
/// same text — unless the file carries an Unsupported marker, because a definition with one was
/// never played whole and its Verified is not a verified play. Every definition here is made up
/// and nothing touches the network or the corpus.
/// </summary>
public sealed class CardFileWriterTests
{
    private const string OracleId = "00000000-0000-0000-0000-0000000000f1";
    private const string OracleHash = "sha256:ffff";

    private static CardDefinition Definition(params Ability[] abilities) => new(
        OracleId: OracleId,
        Name: "Writer Scribe",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Writer Scribe",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Creature, Subtypes: ["Human"]),
                ManaCost: "{1}{U}",
                Power: new PrintedStat("1"),
                Toughness: new PrintedStat("2"),
                Abilities: abilities,
                OracleText: "Writer Scribe"),
        ]);

    private static Ability AtUpkeep(Effect effect) => new TriggeredAbility(new AtStep(Step.Upkeep, new You()), effect);

    private static CardDefinition Clean() => Definition(AtUpkeep(new Draw(new Fixed(1))));

    private static CardDefinition Retranslated() => Definition(AtUpkeep(new Mill(new Fixed(1))));

    private static CardDefinition WithUnsupportedEffect() => Definition(AtUpkeep(new Unsupported("no primitive for this", "draw a card")));

    private static CardDefinition WithUnsupportedContinuous() => Definition(new StaticAbility(new UnsupportedContinuous("no primitive for this", "creatures get +1/+1")));

    private static CardFile FileOf(CardDefinition definition, ImplementationStatus status) => new(
        OracleId,
        "Writer Scribe",
        OracleHash,
        IrVersion.Current,
        definition,
        status,
        new TranslatedProvenance(["model-a", "model-b"], "prompt-v1", Agreed: true),
        ScryfallKeywords: [],
        Scenarios: [],
        Verification: status == ImplementationStatus.Verified ? new Verification("engine:abc", "run-1", 1, 1, DateTimeOffset.UnixEpoch) : null);

    private static CardFileWriter Writer()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "card-file-writer-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return new CardFileWriter(dir);
    }

    [Fact]
    public void A_clean_verified_file_is_kept_over_a_retranslation()
    {
        var writer = Writer();
        Assert.Equal(CardWrite.Written, writer.Write(FileOf(Clean(), ImplementationStatus.Verified)));

        Assert.Equal(CardWrite.KeptVerified, writer.Write(FileOf(Retranslated(), ImplementationStatus.Translated)));

        var onDisk = writer.Load(OracleId)!;
        Assert.Equal(ImplementationStatus.Verified, onDisk.Status);
        Assert.IsType<Draw>(Assert.IsType<TriggeredAbility>(onDisk.Definition.Faces[0].AbilityList[0]).Do);
    }

    [Fact]
    public void A_verified_file_carrying_an_unsupported_marker_is_replaced()
    {
        var writer = Writer();
        Assert.Equal(CardWrite.Written, writer.Write(FileOf(WithUnsupportedEffect(), ImplementationStatus.Verified)));
        Assert.True(writer.LoadLint(OracleId)!.HasErrors);

        Assert.Equal(CardWrite.Written, writer.Write(FileOf(Retranslated(), ImplementationStatus.Translated)));

        var onDisk = writer.Load(OracleId)!;
        Assert.Equal(ImplementationStatus.Translated, onDisk.Status);
        Assert.Null(onDisk.Verification);
        Assert.IsType<Mill>(Assert.IsType<TriggeredAbility>(onDisk.Definition.Faces[0].AbilityList[0]).Do);
        Assert.False(writer.LoadLint(OracleId)!.HasErrors);
    }

    [Fact]
    public void A_verified_file_carrying_an_unsupported_continuous_marker_is_replaced()
    {
        var writer = Writer();
        Assert.Equal(CardWrite.Written, writer.Write(FileOf(WithUnsupportedContinuous(), ImplementationStatus.Verified)));

        Assert.Equal(CardWrite.Written, writer.Write(FileOf(Retranslated(), ImplementationStatus.Disputed)));

        var onDisk = writer.Load(OracleId)!;
        Assert.Equal(ImplementationStatus.Disputed, onDisk.Status);
        Assert.IsType<TriggeredAbility>(onDisk.Definition.Faces[0].AbilityList[0]);
    }
}
