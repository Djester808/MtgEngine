using MtgEngine.Api.Services;

namespace MtgEngine.Cards.Tests;

/// <summary>
/// The keyword enums are generated from the rulebook by tools/gen-vocabulary.js. The API
/// parses the same rulebook. These tests prove the two readings agree, member for member and
/// rule for rule, so a rules release or a generator change cannot leave them apart.
/// </summary>
public sealed class VocabularyTests
{
    private static readonly Lazy<IReadOnlyList<Api.Dtos.KeywordDetailDto>> Parsed = new(() =>
        ComprehensiveRulesParser.Parse(File.ReadAllText(Repo.Path("MtgEngine.Api", "Knowledge", "comprehensive-rules.txt"))).Keywords);

    [Fact]
    public void KeywordKind_matches_the_rulebooks_keyword_abilities()
    {
        var fromRules = Parsed.Value
            .Where(k => k.Category == ComprehensiveRulesParser.KeywordAbility)
            .Select(k => VocabularyNames.ToIdentifier(k.Name))
            .ToHashSet(StringComparer.Ordinal);
        var fromEnum = Enum.GetNames<KeywordKind>().ToHashSet(StringComparer.Ordinal);

        Assert.Empty(fromRules.Except(fromEnum));
        Assert.Empty(fromEnum.Except(fromRules));
    }

    [Fact]
    public void KeywordAction_matches_the_rulebooks_keyword_actions()
    {
        var fromRules = Parsed.Value
            .Where(k => k.Category == ComprehensiveRulesParser.KeywordAction)
            .Select(k => VocabularyNames.ToIdentifier(k.Name))
            .ToHashSet(StringComparer.Ordinal);
        var fromEnum = Enum.GetNames<KeywordAction>().ToHashSet(StringComparer.Ordinal);

        Assert.Empty(fromRules.Except(fromEnum));
        Assert.Empty(fromEnum.Except(fromRules));
    }

    [Fact]
    public void Every_keyword_member_cites_the_rule_the_parser_found()
    {
        var byId = Parsed.Value
            .Where(k => k.Category == ComprehensiveRulesParser.KeywordAbility)
            .ToDictionary(k => VocabularyNames.ToIdentifier(k.Name), k => k.RuleRef, StringComparer.Ordinal);
        foreach (var member in Enum.GetValues<KeywordKind>())
            Assert.Equal(byId[member.ToString()], VocabularyNames.RuleOf(member));
    }

    [Theory]
    [InlineData("First strike", "FirstStrike")]
    [InlineData("Daybound and Nightbound", "DayboundAndNightbound")]
    [InlineData("∞ (Infinity)", "Infinity")]
    [InlineData("For Mirrodin!", "ForMirrodin")]
    [InlineData("Jump-start", "JumpStart")]
    [InlineData("Venture into the Dungeon", "VentureIntoTheDungeon")]
    public void ToIdentifier_matches_the_generator(string name, string expected) =>
        Assert.Equal(expected, VocabularyNames.ToIdentifier(name));

    [Fact]
    public void Scryfall_keyword_strings_resolve_to_keywords_or_actions()
    {
        Assert.Equal(KeywordKind.Flying, VocabularyNames.Keyword("Flying"));
        Assert.Equal(KeywordKind.FirstStrike, VocabularyNames.Keyword("First strike"));
        Assert.Equal(KeywordAction.Scry, VocabularyNames.Action("Scry"));
        Assert.Null(VocabularyNames.Keyword("Landfall"));
    }

    [Fact]
    public void Every_keyword_action_has_an_effect_record_or_a_documented_home()
    {
        // 701 actions that are not resolution-time effects: activate, cast, play (MayCast/MayPlay),
        // tap and untap (two records), and the one whose record is named for its verb form.
        var homes = new Dictionary<KeywordAction, string>
        {
            [KeywordAction.Activate] = nameof(ActivatedAbility),
            [KeywordAction.Cast] = nameof(MayCast),
            [KeywordAction.Play] = nameof(MayPlay),
            [KeywordAction.TapAndUntap] = nameof(Tap),
            [KeywordAction.Attach] = nameof(AttachAction),
            [KeywordAction.Create] = nameof(CreateToken),
            [KeywordAction.Discard] = nameof(Discard),
            [KeywordAction.Explore] = nameof(Explore),
            [KeywordAction.VentureIntoTheDungeon] = nameof(VentureIntoTheDungeon),
            [KeywordAction.OpenAnAttraction] = nameof(OpenAttraction),
            [KeywordAction.RollToVisitYourAttractions] = nameof(RollToVisitAttractions),
            [KeywordAction.TheRingTemptsYou] = nameof(RingTemptsYou),
            [KeywordAction.FaceAVillainousChoice] = nameof(VillainousChoice),
            // Waterbend is a way of paying a cost, CR 701.67, so its home is a Cost record.
            [KeywordAction.Waterbend] = nameof(TapUntappedCost),
        };
        var names = CardFileSerializer.RegisteredNames(typeof(Effect)).Values
            .Concat(CardFileSerializer.RegisteredNames(typeof(Cost)).Values)
            .Concat(CardFileSerializer.RegisteredNames(typeof(Ability)).Values)
            .ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var action in Enum.GetValues<KeywordAction>())
        {
            var expected = homes.GetValueOrDefault(action, action.ToString());
            if (!names.Contains(expected))
                missing.Add($"{action} -> {expected}");
        }

        Assert.True(missing.Count == 0, "Keyword actions without a record: " + string.Join(", ", missing));
    }
}
