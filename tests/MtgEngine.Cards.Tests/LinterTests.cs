namespace MtgEngine.Cards.Tests;

public sealed class LinterTests
{
    [Fact]
    public void A_clean_translated_file_has_no_errors()
    {
        var report = Linter.Check(Fixtures.FileFor(Fixtures.Clamp(), scryfallKeywords: ["Equip"]));
        Assert.False(report.HasErrors, string.Join("; ", report.Findings.Select(f => f.Message)));
    }

    [Fact]
    public void An_unsupported_marker_is_an_error()
    {
        var face = Fixtures.Clamp().Faces[0] with { Abilities = [new SpellAbility(new Unsupported("subgames"))] };
        var file = Fixtures.FileFor(Fixtures.Clamp() with { Faces = [face] });

        var report = Linter.Check(file);
        Assert.Contains(report.Findings, f => f.Code == "L001" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_keyword_that_needs_a_cost_without_one_is_an_error()
    {
        var face = Fixtures.Clamp().Faces[0] with { Abilities = [new KeywordAbility(KeywordKind.Equip)] };
        var report = Linter.Check(Fixtures.FileFor(Fixtures.Clamp() with { Faces = [face] }));

        Assert.Contains(report.Findings, f => f.Code == "L005");
    }

    [Fact]
    public void A_scryfall_keyword_the_definition_lacks_is_an_error()
    {
        var report = Linter.Check(Fixtures.FileFor(Fixtures.Clamp(), scryfallKeywords: ["Equip", "Flying"]));
        Assert.Contains(report.Findings, f => f.Code == "L002" && f.Severity == LintSeverity.Error && f.Message.Contains("Flying", StringComparison.Ordinal));
    }

    [Fact]
    public void A_scryfall_keyword_action_needs_its_effect()
    {
        var report = Linter.Check(Fixtures.FileFor(Fixtures.Within(), scryfallKeywords: ["Scry"]));
        Assert.Contains(report.Findings, f => f.Code == "L002" && f.Message.Contains("Scry", StringComparison.Ordinal));
    }

    [Fact]
    public void A_remembered_key_nothing_produced_is_an_error()
    {
        var face = Fixtures.Within().Faces[0] with
        {
            Abilities = [new SpellAbility(new Move(new Remembered("ghost"), Zone.Hand))],
        };
        var report = Linter.Check(Fixtures.FileFor(Fixtures.Within() with { Faces = [face] }));

        Assert.Contains(report.Findings, f => f.Code == "L003");
    }

    [Fact]
    public void A_remembered_key_from_a_target_is_fine()
    {
        var report = Linter.Check(Fixtures.FileFor(Fixtures.Within()));
        Assert.DoesNotContain(report.Findings, f => f.Code == "L003");
    }

    [Fact]
    public void Verified_needs_a_verification_record_and_scenarios()
    {
        var report = Linter.Check(Fixtures.FileFor(Fixtures.Clamp(), ImplementationStatus.Verified, ["Equip"]));
        Assert.Equal(2, report.Findings.Count(f => f.Code == "L009"));
    }

    [Fact]
    public void An_unknown_counter_kind_is_a_warning()
    {
        var face = Fixtures.Within().Faces[0] with
        {
            Abilities = [new SpellAbility(new PutCounters(new Target(ObjectFilter.Creature), "banana", new Fixed(1)))],
        };
        var report = Linter.Check(Fixtures.FileFor(Fixtures.Within() with { Faces = [face] }));

        Assert.Contains(report.Findings, f => f.Code == "L006" && f.Severity == LintSeverity.Warning);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void Walk_reaches_every_nested_record()
    {
        var nodes = Linter.Walk(Fixtures.Within()).Select(n => n.GetType().Name).ToList();
        Assert.Contains("Destroy", nodes);
        Assert.Contains("TokenSpec", nodes);
        Assert.Contains("Remembered", nodes);
        Assert.Contains("ObjectFilter", nodes);
    }
}
