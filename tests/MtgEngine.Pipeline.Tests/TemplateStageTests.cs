namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// The free slice, built from the Scryfall JSON alone. Every shape the stage claims has a card
/// here whose definition is asserted field by field: a template that reads a line wrongly is a
/// wrong card file with no model to blame, so the shapes are the test.
/// </summary>
public sealed class TemplateStageTests
{
    private static readonly TemplateStage Stage = new();

    // ---- the three templates ------------------------------------------------------------

    [Fact]
    public void A_vanilla_creature_becomes_a_face_with_no_abilities()
    {
        var file = Build(Card("Grizzly Bears", "Creature — Bear", string.Empty, mana: "{1}{G}", power: "2", toughness: "2"));

        var face = Assert.Single(file.Definition.Faces);
        Assert.Equal(FaceRole.Main, face.Role);
        Assert.Equal(CardType.Creature, face.Types.Types);
        Assert.Equal(Supertype.None, face.Types.Supertypes);
        Assert.Equal(["Bear"], face.Types.SubtypeList);
        Assert.Equal("{1}{G}", face.ManaCost);
        Assert.Equal("2", face.Power?.Text);
        Assert.Equal("2", face.Toughness?.Text);
        Assert.Empty(face.AbilityList);
        Assert.Equal("vanilla", Assert.IsType<TemplateProvenance>(file.Source).Template);
        Assert.Equal(ImplementationStatus.Translated, file.Status);
    }

    [Fact]
    public void The_type_line_is_parsed_into_supertypes_types_and_subtypes()
    {
        var file = Build(Card("Rimewood Falls", "Legendary Snow Artifact Creature — Elf Warrior", string.Empty, power: "1", toughness: "1"));

        var types = Assert.Single(file.Definition.Faces).Types;
        Assert.Equal(CardType.Artifact | CardType.Creature, types.Types);
        Assert.Equal(Supertype.Legendary | Supertype.Snow, types.Supertypes);
        Assert.Equal(["Elf", "Warrior"], types.SubtypeList);
    }

    [Fact]
    public void A_basic_land_keeps_its_basic_supertype()
    {
        var file = Build(Card("Forest", "Basic Land — Forest", "{T}: Add {G}."));

        var types = Assert.Single(file.Definition.Faces).Types;
        Assert.Equal(CardType.Land, types.Types);
        Assert.Equal(Supertype.Basic, types.Supertypes);
        Assert.Equal(["Forest"], types.SubtypeList);
        Assert.Equal("mana", Assert.IsType<TemplateProvenance>(file.Source).Template);
    }

    [Fact]
    public void A_tribal_type_line_reads_as_kindred()
    {
        var file = Build(Card("Nameless Inversion", "Tribal Instant — Goblin", string.Empty));

        Assert.Equal(CardType.Kindred | CardType.Instant, Assert.Single(file.Definition.Faces).Types.Types);
    }

    // ---- keywords -----------------------------------------------------------------------

    [Fact]
    public void Comma_separated_keywords_on_one_line_become_one_ability_each()
    {
        var file = Build(Card("Serra Angel", "Creature — Angel", "Flying, vigilance", power: "4", toughness: "4", keywords: ["Flying", "Vigilance"]));

        var abilities = Assert.Single(file.Definition.Faces).AbilityList;
        Assert.Equal(2, abilities.Count);
        Assert.Equal(KeywordKind.Flying, Assert.IsType<KeywordAbility>(abilities[0]).Keyword);
        Assert.Equal(KeywordKind.Vigilance, Assert.IsType<KeywordAbility>(abilities[1]).Keyword);
        Assert.All(abilities, a => Assert.Equal("Flying, vigilance", a.Text));
    }

    [Theory]
    [InlineData("Equip {1}", "Equip", KeywordKind.Equip, "{1}")]
    [InlineData("Cycling {2}", "Cycling", KeywordKind.Cycling, "{2}")]
    [InlineData("Ward {2}", "Ward", KeywordKind.Ward, "{2}")]
    [InlineData("Kicker {1}{R}", "Kicker", KeywordKind.Kicker, "{1}{R}")]
    public void A_cost_keyword_carries_its_printed_cost(string line, string scryfall, KeywordKind kind, string cost)
    {
        var keyword = OnlyKeyword(Card("Parameterised", "Artifact — Equipment", line, keywords: [scryfall]));

        Assert.Equal(kind, keyword.Keyword);
        Assert.Equal(cost, keyword.Cost);
        Assert.Null(keyword.N);
    }

    [Theory]
    [InlineData("Crew 2", "Crew", KeywordKind.Crew, 2)]
    [InlineData("Bushido 1", "Bushido", KeywordKind.Bushido, 1)]
    [InlineData("Annihilator 2", "Annihilator", KeywordKind.Annihilator, 2)]
    public void A_numbered_keyword_carries_its_printed_number(string line, string scryfall, KeywordKind kind, int n)
    {
        var keyword = OnlyKeyword(Card("Numbered", "Artifact — Vehicle", line, power: "4", toughness: "4", keywords: [scryfall]));

        Assert.Equal(kind, keyword.Keyword);
        Assert.Equal(new Fixed(n), keyword.N);
        Assert.Null(keyword.Cost);
    }

    [Fact]
    public void Protection_from_a_color_becomes_a_color_filter()
    {
        var keyword = OnlyKeyword(Card("Warded", "Creature — Knight", "Protection from red", power: "2", toughness: "2", keywords: ["Protection"]));

        Assert.Equal(KeywordKind.Protection, keyword.Keyword);
        Assert.Equal(Color.Red, keyword.Of?.AnyColor);
        Assert.Null(keyword.Of?.AnyTypes);
    }

    [Fact]
    public void Protection_from_two_colors_becomes_both_colors()
    {
        var keyword = OnlyKeyword(Card("Twice Warded", "Creature — Knight", "Protection from red and from white", power: "2", toughness: "2", keywords: ["Protection"]));

        Assert.Equal(Color.Red | Color.White, keyword.Of?.AnyColor);
    }

    [Fact]
    public void Protection_from_a_type_becomes_a_type_filter()
    {
        var keyword = OnlyKeyword(Card("Aloof", "Creature — Spirit", "Protection from creatures", power: "1", toughness: "1", keywords: ["Protection"]));

        Assert.Equal(CardType.Creature, keyword.Of?.AnyTypes);
        Assert.Null(keyword.Of?.AnyColor);
    }

    [Theory]
    [InlineData("Islandwalk", "Island")]
    [InlineData("Forestwalk", "Forest")]
    public void A_landwalk_keyword_names_its_land_subtype(string line, string subtype)
    {
        var keyword = OnlyKeyword(Card("Walker", "Creature — Merfolk", line, power: "1", toughness: "1", keywords: [line]));

        Assert.Equal(KeywordKind.Landwalk, keyword.Keyword);
        Assert.Equal(CardType.Land, keyword.Of?.Types);
        Assert.Equal(subtype, Assert.Single(keyword.Of!.Subtypes!));
    }

    [Fact]
    public void Typecycling_is_cycling_with_the_type_it_fetches()
    {
        var keyword = OnlyKeyword(Card("Cycler", "Creature — Beast", "Forestcycling {2}", power: "3", toughness: "3", keywords: ["Forestcycling"]));

        Assert.Equal(KeywordKind.Cycling, keyword.Keyword);
        Assert.Equal("{2}", keyword.Cost);
        Assert.Equal(CardType.Land, keyword.Of?.Types);
        Assert.Equal("Forest", Assert.Single(keyword.Of!.Subtypes!));
    }

    [Fact]
    public void Daybound_is_the_pair_the_rules_name()
    {
        var keyword = OnlyKeyword(Card("Werewolf", "Creature — Werewolf", "Daybound (If a player casts no spells during their own turn, it becomes night next turn.)", power: "2", toughness: "2", keywords: ["Daybound"]));

        Assert.Equal(KeywordKind.DayboundAndNightbound, keyword.Keyword);
        Assert.Equal("Daybound", keyword.Detail);
    }

    // ---- the six mana patterns ----------------------------------------------------------

    [Fact]
    public void A_printed_mana_symbol_is_fixed_production()
    {
        var mana = ManaOf(Card("Sanctum", "Land", "{T}: Add {C}{C}."));

        Assert.Equal("{C}{C}", mana.Mana.Fixed);
        Assert.False(mana.Mana.AnyColor);
    }

    [Fact]
    public void An_any_color_line_is_any_color()
    {
        var mana = ManaOf(Card("City of Brass", "Land", "{T}: Add one mana of any color."));

        Assert.True(mana.Mana.AnyColor);
        Assert.Null(mana.Mana.Fixed);
    }

    [Fact]
    public void A_commander_identity_line_is_commander_identity()
    {
        var mana = ManaOf(Card("Command Tower", "Land", "{T}: Add one mana of any color in your commander's color identity."));

        Assert.True(mana.Mana.CommanderIdentity);
        Assert.False(mana.Mana.AnyColor);
    }

    [Fact]
    public void An_either_or_line_is_one_of_both_colors()
    {
        var mana = ManaOf(Card("Adarkar Wastes", "Land", "{T}: Add {W} or {U}."));

        Assert.Equal(Color.White | Color.Blue, mana.Mana.OneOf);
        Assert.Null(mana.Mana.Fixed);
    }

    [Fact]
    public void A_pay_one_life_line_adds_the_life_cost()
    {
        var ability = Assert.IsType<ActivatedAbility>(Assert.Single(Build(Card("Caves of Koilos", "Land", "{T}, Pay 1 life: Add {W} or {B}.")).Definition.Faces).AbilityList[0]);

        Assert.Equal(2, ability.Costs.Count);
        Assert.IsType<TapSelf>(ability.Costs[0]);
        Assert.Equal(new Fixed(1), Assert.IsType<PayLife>(ability.Costs[1]).Amount);
        Assert.Equal(Color.White | Color.Black, Assert.IsType<AddMana>(ability.Do).Mana.OneOf);
    }

    [Fact]
    public void An_enters_tapped_land_becomes_a_replacement_ability()
    {
        var abilities = Assert.Single(Build(Card("Salt Marsh", "Land", "This land enters tapped.\n{T}: Add {U} or {B}.")).Definition.Faces).AbilityList;

        Assert.Equal(2, abilities.Count);
        var replacement = Assert.IsType<ReplacementAbility>(abilities[0]);
        Assert.IsType<WouldEnterBattlefield>(replacement.Instead);
        Assert.IsType<EntersTapped>(replacement.With);
        Assert.True(replacement.SelfOnly);
        Assert.Equal("This land enters tapped.", replacement.Text);
        Assert.IsType<ActivatedAbility>(abilities[1]);
    }

    [Fact]
    public void A_mana_ability_taps_the_land_and_keeps_its_printed_line()
    {
        var face = Assert.Single(Build(Card("Forest", "Basic Land — Forest", "{T}: Add {G}.")).Definition.Faces);
        var ability = Assert.IsType<ActivatedAbility>(Assert.Single(face.AbilityList));

        Assert.IsType<TapSelf>(Assert.Single(ability.Costs));
        Assert.Equal("{G}", Assert.IsType<AddMana>(ability.Do).Mana.Fixed);
        Assert.Equal("{T}: Add {G}.", ability.Text);
        Assert.Equal("{T}: Add {G}.", face.OracleText);
    }

    // ---- refusals -----------------------------------------------------------------------

    [Fact]
    public void A_card_with_real_text_is_refused()
    {
        Assert.Null(Stage.Build(Card("Lightning Bolt", "Instant", "Lightning Bolt deals 3 damage to any target.")));
    }

    [Fact]
    public void A_keyword_only_line_the_stage_cannot_spell_is_refused()
    {
        // Scryfall says "Ward" and the corpus's own free-slice check admits the face; the em-dash cost is not a template.
        Assert.Null(Stage.Build(Card("Odd Ward", "Creature — Sphinx", "Ward—Pay 3 life.", power: "4", toughness: "4", keywords: ["Ward"])));
    }

    [Fact]
    public void A_type_line_word_the_vocabulary_does_not_know_is_refused()
    {
        Assert.Null(Stage.Build(Card("Impossible", "Summon — Legend", string.Empty, power: "1", toughness: "1")));
    }

    // ---- files --------------------------------------------------------------------------

    [Fact]
    public void Every_template_shape_lints_clean_and_round_trips()
    {
        foreach (var card in Fixtures)
        {
            var file = Build(card);
            var report = Linter.Check(file);
            Assert.False(report.HasErrors, card.Name + ": " + string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));

            var json = CardFileSerializer.Write(file);
            Assert.Equal(json, CardFileSerializer.Write(CardFileSerializer.Read(json)));
        }
    }

    [Fact]
    public void Run_writes_one_file_per_oracle_id()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "template-stage-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var run = Stage.Run(Fixtures, dir);

            Assert.Equal(Fixtures.Count, run.Written);
            Assert.Equal(0, run.Refused);
            Assert.Empty(run.Notes);
            foreach (var card in Fixtures)
            {
                var path = Path.Combine(dir, card.OracleId + ".json");
                Assert.True(File.Exists(path), path);
                Assert.Equal(card.OracleId, CardFileSerializer.Read(File.ReadAllText(path)).OracleId);
            }
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_status_option_promotes_a_clean_template_to_verified()
    {
        var file = new TemplateStage(ImplementationStatus.Verified).Build(Card("Forest", "Basic Land — Forest", "{T}: Add {G}."))!;

        Assert.Equal(ImplementationStatus.Verified, file.Status);
        Assert.Equal("template", file.Verification?.EngineHash);
        Assert.Equal("template", file.Verification?.RunId);
        Assert.Equal(DateTimeOffset.UnixEpoch, file.Verification?.At);
    }

    [Fact]
    public void A_template_carries_the_scryfall_keywords_it_was_built_from()
    {
        var file = Build(Card("Serra Angel", "Creature — Angel", "Flying, vigilance", power: "4", toughness: "4", keywords: ["Flying", "Vigilance"]));

        Assert.Equal(["Flying", "Vigilance"], file.KeywordList);
        Assert.Equal("keyword-only", Assert.IsType<TemplateProvenance>(file.Source).Template);
    }

    // ---- tokens -------------------------------------------------------------------------

    [Fact]
    public void A_token_with_keyword_only_text_gets_its_abilities()
    {
        var result = Templates.Token(new CorpusToken("tok-1", "Angel", "Token Creature — Angel", "W", "Flying, vigilance", "4", "4"))!;

        Assert.Null(result.Note);
        Assert.Equal("Angel", result.Spec.Name);
        Assert.Equal(Color.White, result.Spec.Colors);
        Assert.Equal(CardType.Creature, result.Spec.Types);
        Assert.Equal("Angel", Assert.Single(result.Spec.Subtypes!));
        Assert.Equal(4, result.Spec.Power);
        Assert.Equal(4, result.Spec.Toughness);
        Assert.Equal("tok-1", result.Spec.ScryfallOracleId);
        Assert.Equal(
            [KeywordKind.Flying, KeywordKind.Vigilance],
            result.Spec.Abilities!.Select(a => ((KeywordAbility)a).Keyword));
    }

    [Fact]
    public void A_token_with_no_text_has_no_abilities_and_no_note()
    {
        var result = Templates.Token(new CorpusToken("tok-2", "Servo", "Token Artifact Creature — Servo", string.Empty, string.Empty, "1", "1"))!;

        Assert.Null(result.Note);
        Assert.Null(result.Spec.Abilities);
        Assert.Equal(Color.Colorless, result.Spec.Colors);
        Assert.Equal(CardType.Artifact | CardType.Creature, result.Spec.Types);
    }

    [Fact]
    public void A_token_whose_text_is_not_keyword_only_gets_a_note_instead_of_abilities()
    {
        var result = Templates.Token(new CorpusToken("tok-3", "Treasure", "Token Artifact — Treasure", string.Empty, "{T}, Sacrifice this token: Add one mana of any color.", null, null))!;

        Assert.NotNull(result.Note);
        Assert.Null(result.Spec.Abilities);
        Assert.Equal("Treasure", result.Spec.Name);
        Assert.Null(result.Spec.Power);
    }

    [Fact]
    public void A_starred_token_stat_is_left_as_no_number()
    {
        var result = Templates.Token(new CorpusToken("tok-4", "Ooze", "Token Creature — Ooze", "G", string.Empty, "*", "*"))!;

        Assert.Null(result.Spec.Power);
        Assert.Null(result.Spec.Toughness);
    }

    // ---- fixtures -----------------------------------------------------------------------

    /// <summary>One card per shape the stage claims, reused by the file-level tests.</summary>
    internal static IReadOnlyList<CorpusCard> Fixtures { get; } =
    [
        Card("Grizzly Bears", "Creature — Bear", string.Empty, mana: "{1}{G}", power: "2", toughness: "2"),
        Card("Serra Angel", "Creature — Angel", "Flying, vigilance", mana: "{3}{W}{W}", power: "4", toughness: "4", keywords: ["Flying", "Vigilance"]),
        Card("Parameterised", "Artifact — Equipment", "Equip {1}", mana: "{0}", keywords: ["Equip"]),
        Card("Numbered", "Artifact — Vehicle", "Crew 2", mana: "{2}", power: "4", toughness: "4", keywords: ["Crew"]),
        Card("Warded", "Creature — Knight", "Protection from red", mana: "{1}{W}", power: "2", toughness: "2", keywords: ["Protection"]),
        Card("Walker", "Creature — Merfolk", "Islandwalk", mana: "{1}{U}", power: "1", toughness: "1", keywords: ["Islandwalk"]),
        Card("Forest", "Basic Land — Forest", "{T}: Add {G}."),
        Card("Sanctum", "Land", "{T}: Add {C}{C}."),
        Card("Adarkar Wastes", "Land", "{T}: Add {W} or {U}."),
        Card("City of Brass", "Land", "{T}: Add one mana of any color."),
        Card("Command Tower", "Land", "{T}: Add one mana of any color in your commander's color identity."),
        Card("Caves of Koilos", "Land", "{T}, Pay 1 life: Add {W} or {B}."),
        Card("Salt Marsh", "Land", "This land enters tapped.\n{T}: Add {U} or {B}."),
    ];

    private static CardFile Build(CorpusCard card)
    {
        var file = Stage.Build(card);
        Assert.NotNull(file);
        return file;
    }

    private static KeywordAbility OnlyKeyword(CorpusCard card) =>
        Assert.IsType<KeywordAbility>(Assert.Single(Assert.Single(Build(card).Definition.Faces).AbilityList));

    private static AddMana ManaOf(CorpusCard card) =>
        Assert.IsType<AddMana>(Assert.IsType<ActivatedAbility>(Assert.Single(Assert.Single(Build(card).Definition.Faces).AbilityList)).Do);

    private static CorpusCard Card(
        string name,
        string typeLine,
        string text,
        string? mana = null,
        string? power = null,
        string? toughness = null,
        IReadOnlyList<string>? keywords = null) => new(
            "oracle-" + name.Replace(' ', '-').ToLowerInvariant(),
            name,
            "normal",
            [new CorpusFace(name, typeLine, mana, text, power, toughness, null, null)],
            keywords ?? [],
            []);
}
