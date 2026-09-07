using MtgEngine.Cards.Pipeline.Agree;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>Wave 15 (P17): three readings in, any two that say the same thing decide the card.</summary>
public sealed partial class AgreeStageTests
{
    private static CardDefinition DrawingTwo() => Definition(new Draw(new Fixed(2)));

    [Fact]
    public void Two_of_three_identical_clean_readings_agree_and_the_odd_reading_is_outvoted_not_a_candidate()
    {
        var result = AgreeStage.Agree(Request(
            new ModelReading("model-a", Drawing()),
            new ModelReading("model-b", Milling()),
            new ModelReading("model-c", Drawing())));

        Assert.Equal(ImplementationStatus.Translated, result.Status);
        Assert.True(result.Agreed);
        Assert.Equal(new[] { "model-a", "model-c" }, result.AgreedBy.ToArray());
        Assert.Null(result.Difference);

        var file = Assert.IsType<CardFile>(result.File);
        Assert.Equal(new[] { "model-a", "model-c" }, Assert.IsType<TranslatedProvenance>(file.Source).Models.ToArray());
        Assert.True(file.Candidates is null || file.Candidates.Count == 0);

        var odd = Assert.Single(result.OutvotedReadings);
        Assert.Equal("model-b", odd.Model);
        Assert.NotNull(odd.Difference);
        Assert.Contains("draw", odd.Difference, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_of_three_agree_when_the_third_did_not_parse_and_its_error_is_what_is_reported()
    {
        var result = AgreeStage.Agree(Request(
            new ModelReading("model-a", Drawing()),
            new ModelReading("model-b", null, "not a CardDefinition"),
            new ModelReading("model-c", Drawing())));

        Assert.Equal(ImplementationStatus.Translated, result.Status);
        Assert.Equal(new[] { "model-a", "model-c" }, result.AgreedBy.ToArray());
        var odd = Assert.Single(result.OutvotedReadings);
        Assert.Equal(new OutvotedReading("model-b", "not a CardDefinition"), odd);
    }

    [Fact]
    public void Three_readings_of_which_no_two_agree_are_disputed_with_every_reading_a_candidate()
    {
        var result = AgreeStage.Agree(Request(
            new ModelReading("model-a", Drawing()),
            new ModelReading("model-b", Milling()),
            new ModelReading("model-c", DrawingTwo())));

        Assert.Equal(ImplementationStatus.Disputed, result.Status);
        Assert.Empty(result.AgreedBy);
        Assert.Empty(result.OutvotedReadings);
        Assert.NotNull(result.Difference);
        Assert.Equal(3, result.File?.Candidates?.Count);
    }

    [Fact]
    public void Two_readings_are_judged_as_before_both_must_say_the_same_thing()
    {
        var disputed = AgreeStage.Agree(Request(
            new ModelReading("model-a", Drawing()),
            new ModelReading("model-b", Milling())));
        Assert.Equal(ImplementationStatus.Disputed, disputed.Status);
        Assert.Empty(disputed.OutvotedReadings);

        var agreed = AgreeStage.Agree(Request(
            new ModelReading("model-a", Drawing()),
            new ModelReading("model-b", Drawing())));
        Assert.Equal(new[] { "model-a", "model-b" }, agreed.AgreedBy.ToArray());
        Assert.Empty(agreed.OutvotedReadings);
    }

    [Fact]
    public void The_report_counts_agreements_per_pair_largest_first()
    {
        var results = new[]
        {
            AgreeStage.Agree(Request(new ModelReading("model-a", Drawing()), new ModelReading("model-b", Milling()), new ModelReading("model-c", Drawing()))),
            AgreeStage.Agree(Request(new ModelReading("model-a", Drawing()), new ModelReading("model-b", Drawing()), new ModelReading("model-c", Drawing()))),
            AgreeStage.Agree(Request(new ModelReading("model-a", Drawing()), new ModelReading("model-b", Milling()), new ModelReading("model-c", DrawingTwo()))),
        };

        var report = AgreeReport.Of(results);

        Assert.Equal(2, report.Agreed);
        Assert.Equal(1, report.Disputed);
        Assert.Equal(
            new[] { new PairAgreement("model-a", "model-c", 2), new PairAgreement("model-a", "model-b", 1), new PairAgreement("model-b", "model-c", 1) },
            report.AgreedByPair.ToArray());
        Assert.Contains("  agreed by pair: model-a+model-c: 2, model-a+model-b: 1, model-b+model-c: 1", report.Text(), StringComparison.Ordinal);
    }
}
