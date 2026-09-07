using MtgEngine.Cards.Pipeline.Agree;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// The agreement stage, the dispute diff and the card-file writer. Every definition here is
/// made up: no real card is named, and nothing in this file touches the network or the corpus.
/// </summary>
public sealed class AgreeStageTests
{
    private const string OracleId = "00000000-0000-0000-0000-0000000000a1";
    private const string OracleHash = "sha256:aaaa";
    private const string OtherHash = "sha256:bbbb";

    /// <summary>A made-up creature: flying, then a spell-shaped second ability whose effect the caller chooses.</summary>
    private static CardDefinition Definition(Effect second) => new(
        OracleId: OracleId,
        Name: "Test Scribe",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Test Scribe",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Creature, Subtypes: ["Human", "Wizard"]),
                ManaCost: "{1}{U}",
                Power: new PrintedStat("1"),
                Toughness: new PrintedStat("2"),
                Abilities:
                [
                    new KeywordAbility(KeywordKind.Flying) { Text = "Flying" },
                    new TriggeredAbility(new AtStep(Step.Upkeep, new You()), second) { Text = "At the beginning of your upkeep, draw a card." },
                ],
                OracleText: "Flying\nAt the beginning of your upkeep, draw a card."),
        ]);

    private static CardDefinition Drawing() => Definition(new Draw(new Fixed(1)));

    private static CardDefinition Milling() => Definition(new Mill(new Fixed(1)));

    private static AgreeRequest Request(params ModelReading[] readings) => new(
        OracleId,
        "Test Scribe",
        OracleHash,
        "prompt-v1",
        readings,
        ScryfallKeywords: ["Flying"]);

    private static string Scratch()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "agree-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Two_clean_identical_readings_are_translated_and_agreed()
    {
        var result = AgreeStage.Agree(Request(
            new ModelReading("model-a", Drawing()),
            new ModelReading("model-b", Drawing())));

        Assert.Equal(ImplementationStatus.Translated, result.Status);
        Assert.True(result.Agreed);
        Assert.Null(result.Difference);
        Assert.Equal(["model-a", "model-b"], result.CleanModels);

        var file = Assert.IsType<CardFile>(result.File);
        Assert.Null(file.Candidates);
        Assert.Equal(OracleHash, file.OracleHash);
        var source = Assert.IsType<TranslatedProvenance>(file.Source);
        Assert.True(source.Agreed);
        Assert.Equal(["model-a", "model-b"], source.Models);
        Assert.Equal("prompt-v1", source.PromptVersion);
    }

    /// <summary>The printed text is not a disagreement: two models quoting the card differently still agree.</summary>
    [Fact]
    public void Readings_that_differ_only_in_printed_text_still_agree()
    {
        var quoted = Drawing() with
        {
            Faces = [Drawing().Faces[0] with { OracleText = "Flying\nAt the beginning of your upkeep, draw a card. (Reminder.)" }],
        };

        var result = AgreeStage.Agree(Request(
            new ModelReading("model-a", Drawing()),
            new ModelReading("model-b", quoted)));

        Assert.Equal(ImplementationStatus.Translated, result.Status);
        Assert.Null(result.Difference);
    }

    [Fact]
    public void Readings_that_differ_are_disputed_and_keep_both_candidates()
    {
        var result = AgreeStage.Agree(Request(
            new ModelReading("model-a", Drawing()),
            new ModelReading("model-b", Milling())));

        Assert.Equal(ImplementationStatus.Disputed, result.Status);
        Assert.False(result.Agreed);
        Assert.Equal("faces[0].abilities[1].do.kind: \"Draw\" vs \"Mill\"", result.Difference);

        var file = Assert.IsType<CardFile>(result.File);
        Assert.Equal(["model-a", "model-b"], file.Candidates!.Select(c => c.Model));
        Assert.Equal(2, file.Candidates!.Count);

        // The first clean reading is the definition the file carries.
        var written = Assert.IsType<TriggeredAbility>(file.Definition.Faces[0].AbilityList[1]);
        Assert.IsType<Draw>(written.Do);
        Assert.False(Assert.IsType<TranslatedProvenance>(file.Source).Agreed);
    }

    /// <summary>An Unsupported marker is a lint error (L001), so that reading is not evidence and the card is disputed.</summary>
    [Fact]
    public void One_clean_reading_and_one_that_lints_dirty_is_disputed_on_the_clean_one()
    {
        var unsupported = Definition(new Unsupported("no primitive for this", "draw a card"));

        var result = AgreeStage.Agree(Request(
            new ModelReading("model-a", unsupported),
            new ModelReading("model-b", Drawing())));

        Assert.Equal(ImplementationStatus.Disputed, result.Status);
        Assert.Equal(["model-b"], result.CleanModels);
        Assert.True(result.Lint["model-a"]!.HasErrors);
        Assert.Contains(result.Lint["model-a"]!.Findings, f => f.Code == "L001");
        Assert.False(result.Lint["model-b"]!.HasErrors);

        var file = Assert.IsType<CardFile>(result.File);
        Assert.IsType<Draw>(Assert.IsType<TriggeredAbility>(file.Definition.Faces[0].AbilityList[1]).Do);
        Assert.Equal(2, file.Candidates!.Count);
    }

    /// <summary>The keyword cross-check is the linter's L002: Scryfall says Flying and the reading has none.</summary>
    [Fact]
    public void A_reading_that_drops_a_scryfall_keyword_is_not_clean()
    {
        var noFlying = Drawing() with
        {
            Faces = [Drawing().Faces[0] with { Abilities = [new TriggeredAbility(new AtStep(Step.Upkeep, new You()), new Draw(new Fixed(1)))] }],
        };

        var result = AgreeStage.Agree(Request(
            new ModelReading("model-a", noFlying),
            new ModelReading("model-b", Drawing())));

        Assert.Equal(ImplementationStatus.Disputed, result.Status);
        Assert.Equal(["model-b"], result.CleanModels);
        Assert.Contains(result.Lint["model-a"]!.Findings, f => f.Code == "L002");
    }

    [Fact]
    public void No_reading_that_parses_leaves_the_card_untranslated_with_no_file()
    {
        var result = AgreeStage.Agree(Request(
            new ModelReading("model-a", null, "unterminated object at 512"),
            new ModelReading("model-b", null, "expected a value")));

        Assert.Equal(ImplementationStatus.Untranslated, result.Status);
        Assert.Null(result.File);
        Assert.Empty(result.CleanModels);
        Assert.Null(result.Lint["model-a"]);
        Assert.Null(result.Difference);
    }

    [Fact]
    public void Agreement_needs_two_readings()
    {
        var request = Request(new ModelReading("model-a", Drawing()));
        Assert.Throws<ArgumentException>(() => AgreeStage.Agree(request));
    }

    [Fact]
    public void The_diff_reports_a_missing_property_and_a_length_difference()
    {
        var oneAbility = Drawing() with
        {
            Faces = [Drawing().Faces[0] with { Abilities = [new KeywordAbility(KeywordKind.Flying)] }],
        };
        Assert.Equal("faces[0].abilities: 2 items vs 1 items", JsonDiff.Between(Drawing(), oneAbility));

        var noManaCost = Drawing() with { Faces = [Drawing().Faces[0] with { ManaCost = null }] };
        Assert.Equal("faces[0].manaCost: \"{1}{U}\" vs absent", JsonDiff.Between(Drawing(), noManaCost));
        Assert.Equal("faces[0].manaCost: absent vs \"{1}{U}\"", JsonDiff.Between(noManaCost, Drawing()));
        Assert.Null(JsonDiff.Between(Drawing(), Drawing()));
    }

    [Fact]
    public void The_writer_keeps_a_verified_file_when_the_oracle_hash_is_unchanged()
    {
        var writer = new CardFileWriter(Scratch());
        Assert.Equal(CardWrite.Written, writer.Write(Verified(OracleHash)));

        var translated = AgreeStage.Agree(Request(
            new ModelReading("model-a", Milling()),
            new ModelReading("model-b", Milling()))).File!;
        Assert.Equal(CardWrite.KeptVerified, writer.Write(translated));

        var onDisk = writer.Load(OracleId)!;
        Assert.Equal(ImplementationStatus.Verified, onDisk.Status);
        Assert.IsType<Draw>(Assert.IsType<TriggeredAbility>(onDisk.Definition.Faces[0].AbilityList[1]).Do);
    }

    [Fact]
    public void The_writer_accepts_a_lower_status_when_the_oracle_hash_changed()
    {
        var writer = new CardFileWriter(Scratch());
        writer.Write(Verified(OracleHash));

        var reread = AgreeStage.Agree(new AgreeRequest(
            OracleId,
            "Test Scribe",
            OtherHash,
            "prompt-v1",
            [new ModelReading("model-a", Milling()), new ModelReading("model-b", Milling())],
            ["Flying"])).File!;
        Assert.Equal(ImplementationStatus.Translated, reread.Status);
        Assert.Equal(CardWrite.Written, writer.Write(reread));

        var onDisk = writer.Load(OracleId)!;
        Assert.Equal(ImplementationStatus.Translated, onDisk.Status);
        Assert.Equal(OtherHash, onDisk.OracleHash);
        Assert.IsType<Mill>(Assert.IsType<TriggeredAbility>(onDisk.Definition.Faces[0].AbilityList[1]).Do);
    }

    [Fact]
    public void A_verified_file_is_replaced_by_a_newer_verification()
    {
        var writer = new CardFileWriter(Scratch());
        writer.Write(Verified(OracleHash));
        var again = Verified(OracleHash) with { Definition = Milling() };
        Assert.Equal(CardWrite.Written, writer.Write(again));
        Assert.IsType<Mill>(Assert.IsType<TriggeredAbility>(writer.Load(OracleId)!.Definition.Faces[0].AbilityList[1]).Do);
    }

    [Fact]
    public void The_write_is_atomic_and_leaves_no_temporary_file_behind()
    {
        var dir = Scratch();
        var writer = new CardFileWriter(dir);
        var file = AgreeStage.Agree(Request(
            new ModelReading("model-a", Drawing()),
            new ModelReading("model-b", Drawing()))).File!;

        Assert.Equal(CardWrite.Written, writer.Write(file));

        Assert.True(File.Exists(writer.FileFor(OracleId)));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(dir, Path.GetDirectoryName(writer.FileFor(OracleId)));

        // The lint report lands beside the file, under cards/.lint/.
        Assert.True(File.Exists(writer.LintFileFor(OracleId)));
        Assert.Equal(Path.Combine(dir, CardFileWriter.LintDirectoryName), Path.GetDirectoryName(writer.LintFileFor(OracleId)));
        Assert.False(writer.LoadLint(OracleId)!.HasErrors);

        // And the file round-trips: the writer is the only writer of cards/.
        var onDisk = writer.Load(OracleId)!;
        Assert.Equal(file.Name, onDisk.Name);
        Assert.Equal(JsonDiff.CanonicalText(file.Definition), JsonDiff.CanonicalText(onDisk.Definition));
    }

    [Fact]
    public void Loading_a_card_that_was_never_written_is_null()
    {
        var writer = new CardFileWriter(Scratch());
        Assert.Null(writer.Load(OracleId));
        Assert.Null(writer.LoadLint(OracleId));
    }

    [Fact]
    public void An_untranslated_result_writes_nothing()
    {
        var dir = Scratch();
        var writer = new CardFileWriter(dir);
        var result = AgreeStage.Agree(Request(
            new ModelReading("model-a", null, "bad json"),
            new ModelReading("model-b", null, "bad json")));

        Assert.Null(writer.Write(result));
        Assert.Empty(Directory.GetFiles(dir));
    }

    private static CardFile Verified(string hash) => new(
        OracleId,
        "Test Scribe",
        hash,
        IrVersion.Current,
        Drawing(),
        ImplementationStatus.Verified,
        new TranslatedProvenance(["model-a", "model-b"], "prompt-v1", Agreed: true),
        ScryfallKeywords: ["Flying"],
        Scenarios: [],
        Verification: new Verification("engine:abc", "run-1", 1, 1, DateTimeOffset.UnixEpoch));
}
