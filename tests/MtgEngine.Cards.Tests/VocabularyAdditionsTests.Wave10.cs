namespace MtgEngine.Cards.Tests;

/// <summary>
/// Wave 10 (seam-requests/V7.md). Three things, none of them a new record: CR 205.3i's basic land
/// types become generated rules data beside CR 205.3m's creature types, so the engine stops holding
/// a literal copy of them; the mana colour a scenario's activation chooses (CR 608.2d) gets the lint
/// rule its free-text field needs; and the two enchant shapes the wave-9 table declined a rule for
/// are asserted to stay lint clean, so a later pass cannot add one back by reading E5d alone.
/// </summary>
public sealed partial class VocabularyAdditionsTests
{
    /// <summary>An Aura whose enchant ability is spelled the way the test names it. CR 702.5b.</summary>
    private static CardDefinition Wave10Aura(KeywordAbility enchant) => new(
        OracleId: "00000000-0000-0000-0000-0000000000b1",
        Name: "Test Wave Ten Aura",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Test Wave Ten Aura",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Enchantment, Subtypes: ["Aura"]),
                ManaCost: "{1}{W}",
                Abilities: [enchant],
                OracleText: "Enchant."),
        ]);

    /// <summary>A lint-clean file whose one scenario activates an ability with the colours given.</summary>
    private static CardFile Wave10FileActivating(string? manaColors)
    {
        var file = Fixtures.FileFor(Fixtures.Clamp(), scryfallKeywords: ["Equip"]);
        var scenario = new Scenario(
            Id: "wave10-activate",
            Title: "An activation that adds one mana of any colour",
            Source: new GeneratedSource("VocabularyAdditionsTests"),
            Setup: new ScenarioSetup([new PlayerSetup("A"), new PlayerSetup("B")], "A"),
            Steps: [new ActivateStep("A", "Test Clamp", 2, ManaColors: manaColors)],
            Expect: []);

        return file with { Scenarios = [scenario] };
    }

    [Fact]
    public void The_generated_set_holds_the_five_basic_land_types_of_CR_205_3i()
    {
        Assert.Equal(5, LandTypes.Basic.Count);
        foreach (var name in new[] { "Forest", "Island", "Mountain", "Plains", "Swamp" })
        {
            Assert.Contains(name, LandTypes.Basic.AsEnumerable());
            Assert.True(LandTypes.IsBasicLandType(name));
        }
    }

    [Fact]
    public void A_land_type_that_is_not_basic_is_not_in_the_generated_set()
    {
        // CR 205.3i lists seventeen land types and then names five of them basic. "Urza's" carries
        // the rulebook's typographic apostrophe; neither spelling of it is a basic land type.
        Assert.DoesNotContain("Urza’s", LandTypes.Basic.AsEnumerable());
        Assert.False(LandTypes.IsBasicLandType("Urza’s"));
        Assert.False(LandTypes.IsBasicLandType("Urza's"));
        Assert.False(LandTypes.IsBasicLandType("Desert"));
        Assert.False(LandTypes.IsBasicLandType("Power-Plant"));
        // CR 205.4a: Basic is a supertype, and the question here is about subtypes only.
        Assert.False(LandTypes.IsBasicLandType("Basic"));
        Assert.False(LandTypes.IsBasicLandType(string.Empty));
    }

    [Fact]
    public void Basic_land_types_are_matched_without_regard_to_case()
    {
        Assert.True(LandTypes.IsBasicLandType("forest"));
        Assert.True(LandTypes.IsBasicLandType("MOUNTAIN"));
    }

    [Fact]
    public void The_generated_set_is_the_clause_CR_205_3i_gives()
    {
        var fromRules = BasicLandTypesFromTheRulebook();

        Assert.Equal(5, fromRules.Count);
        Assert.Empty(fromRules.Except(LandTypes.Basic, StringComparer.Ordinal));
        Assert.Empty(LandTypes.Basic.Except(fromRules, StringComparer.Ordinal));
        // The clause ends "... and Swamp are the basic land types", so the last name is read
        // without its conjunction.
        Assert.Contains("Swamp", fromRules);
    }

    [Fact]
    public void An_activate_step_whose_mana_colours_are_not_colours_is_an_error()
    {
        // X is a generic symbol, not a colour (CR 107.3); a step naming it would be handed to the
        // runner as a chosen colour and refused there, one indirection away from the cause.
        var report = Linter.Check(Wave10FileActivating("WX"));

        Assert.Contains(report.Findings, f => f is { Severity: LintSeverity.Error, Code: "L017" });
        Assert.Contains(report.Findings, f => f.Path == "scenarios[0].steps[0].manaColors");
    }

    [Fact]
    public void An_activate_step_with_no_mana_colours_at_all_is_an_error()
    {
        // An empty string is not "chooses nothing" — null is. An empty one is a dropped value.
        Assert.Contains(Linter.Check(Wave10FileActivating(string.Empty)).Findings, f => f.Code == "L017");
    }

    [Fact]
    public void An_activate_step_naming_one_letter_per_mana_is_clean()
    {
        // CR 608.2d: two letters for an ability that adds two mana of any colour, in the order added.
        var report = Linter.Check(Wave10FileActivating("WU"));

        Assert.DoesNotContain(report.Findings, f => f.Code == "L017");
        Assert.False(report.HasErrors, string.Join("; ", report.Findings.Select(f => f.Message)));
    }

    [Fact]
    public void An_activate_step_that_chooses_no_colour_is_clean()
    {
        var report = Linter.Check(Wave10FileActivating(null));

        Assert.DoesNotContain(report.Findings, f => f.Code == "L017");
        Assert.False(report.HasErrors, string.Join("; ", report.Findings.Select(f => f.Message)));
    }

    [Fact]
    public void An_enchant_with_both_halves_set_is_legal()
    {
        // "Enchant creature or player" is printed (CR 702.5b), so both halves set is one card and
        // not two restrictions anded together.
        var enchant = new KeywordAbility(
            KeywordKind.Enchant,
            Of: new ObjectFilter(Types: CardType.Creature),
            OfPlayer: new PlayerFilter());

        var report = Linter.Check(Fixtures.FileFor(Wave10Aura(enchant), scryfallKeywords: ["Enchant"]));

        Assert.False(report.HasErrors, string.Join("; ", report.Findings.Select(f => f.Message)));
    }

    [Fact]
    public void An_enchant_with_neither_half_set_is_legal()
    {
        // The wave-9 table declined a pair rule for this: neither half set is "enchant anything",
        // which the engine cannot tell from a translation that dropped a filter — and guessing
        // would refuse real cards. seam-requests/E5d.md §2.
        var report = Linter.Check(Fixtures.FileFor(Wave10Aura(new KeywordAbility(KeywordKind.Enchant)), scryfallKeywords: ["Enchant"]));

        Assert.False(report.HasErrors, string.Join("; ", report.Findings.Select(f => f.Message)));
        Assert.DoesNotContain(report.Findings, f => f.Code == "L005");
    }

    [Fact]
    public void An_enchant_that_names_only_a_player_is_legal()
    {
        var enchant = new KeywordAbility(KeywordKind.Enchant, OfPlayer: new PlayerFilter(Opponent: true));

        var report = Linter.Check(Fixtures.FileFor(Wave10Aura(enchant), scryfallKeywords: ["Enchant"]));

        Assert.False(report.HasErrors, string.Join("; ", report.Findings.Select(f => f.Message)));
    }

    /// <summary>
    /// CR 205.3i in the rulebook's own words: the clause after "Of that list," up to its full stop
    /// names the basic land types. Read here the way the generator reads it, so the two agree by
    /// construction rather than by a second copy of the list.
    /// </summary>
    private static List<string> BasicLandTypesFromTheRulebook()
    {
        var line = File.ReadLines(Repo.Path("MtgEngine.Api", "Knowledge", "comprehensive-rules.txt"))
            .Select(l => l.Trim())
            .First(l => l.StartsWith("205.3i", StringComparison.Ordinal));

        var clause = line.Split("Of that list,", StringSplitOptions.None)[1].Split('.')[0];
        clause = clause.Replace("are the basic land types", string.Empty, StringComparison.Ordinal);

        var types = new List<string>();
        foreach (var part in clause.Split(','))
        {
            var name = part.Trim();
            if (name.StartsWith("and ", StringComparison.OrdinalIgnoreCase))
                name = name[4..].Trim();
            if (name.Length > 0)
                types.Add(name);
        }

        return types;
    }
}
