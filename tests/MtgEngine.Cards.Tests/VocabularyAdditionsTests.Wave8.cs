using System.Text.Json.Nodes;

namespace MtgEngine.Cards.Tests;

/// <summary>
/// The wave-8 additions (seam-requests/V6.md): the reasons the 200-card pilot still could not say
/// after V3-V5 landed. A cost offered while an effect resolves (CR 118.12), a permanent tapped for
/// mana (CR 106.12a), the mana that produced spent later (CR 106.6), a token created this turn
/// (CR 111.1), a land card with a basic land type (CR 205.3i), and an activation cost restricted to
/// mana from a named source (CR 106.6b). Each has to be in the name table, in the schema the
/// translator is constrained to, and lint clean, or a card carrying it is lost silently.
/// </summary>
public sealed class VocabularyAdditionsWave8Tests
{
    // CR 118.12: "[A player] may [do something]. If [that player] does, [effect]."
    private static SpellAbility V6MayPay() =>
        new(new MayPay(new ManaCost("{4}"), new Draw(new Fixed(2)), Else: new LoseLife(new Fixed(2))));

    // CR 106.12a with CR 106.6: tapped for mana of the chosen colour, and the extra mana it makes
    // carries a key so a later delayed trigger can say "that mana" (CR 603.7a).
    private static TriggeredAbility V6TappedForChosenColor() =>
        new(
            new TappedForMana(new ObjectFilter(Types: CardType.Land, ControlledBy: new You()), OfChosenColorKey: "chosen"),
            new Sequence(
            [
                new ChooseValue(ChooseWhat.Color, "chosen"),
                new AddMana(new ManaProduction(ChosenColorKey: "chosen"), Key: "extra"),
                new DelayedTrigger(new ManaSpent("extra", new ObjectFilter(Spell: true)), new Draw(new Fixed(1))),
            ]));

    // CR 111.1 beside CR 205.3i: a condition on this turn's tokens and a filter for a basic land type.
    private static TriggeredAbility V6CreatedTokenAndBasicLandType() =>
        new(
            new AtStep(Step.Upkeep, new You()),
            new Search(new ObjectFilter(Types: CardType.Land, HasBasicLandType: true), Zone.Battlefield, Tapped: true),
            If: new CreatedTokenThisTurn(new You()));

    // CR 106.6b: "activate only using mana produced by artifacts" names a source, not a colour.
    private static ActivatedAbility V6PaidOnlyFromArtifacts() =>
        new(
            [new ManaCost("{2}")],
            new Draw(new Fixed(1)),
            PayOnlyFrom: new ObjectFilter(Types: CardType.Artifact, ControlledBy: new You()));

    private static CardDefinition V6DefinitionWith(params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-0000000000f6",
        Name: "Wave Eight Addition",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Wave Eight Addition",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Creature, Subtypes: ["Shapeshifter"]),
                ManaCost: "{1}{U}",
                Abilities: abilities,
                OracleText: "Wave eight text.")
        ]);

    private static CardFile V6FileWith(params Ability[] abilities)
    {
        var definition = V6DefinitionWith(abilities);
        return new CardFile(
            OracleId: definition.OracleId,
            Name: definition.Name,
            OracleHash: OracleHashing.Of(definition.Faces.Select(f => f.OracleText)),
            IrVersion: IrVersion.Current,
            Definition: definition,
            Status: ImplementationStatus.Translated,
            Source: new TemplateProvenance("test"));
    }

    private static CardDefinition V6RoundTrip(CardDefinition definition) =>
        CardFileSerializer.ReadDefinition(CardFileSerializer.WriteDefinition(definition));

    private static LintReport V6Lint(params Ability[] abilities) => Linter.Check(V6FileWith(abilities));

    [Fact]
    public void MayPay_is_registered_under_Effect()
    {
        var registered = CardFileSerializer.RegisteredNames(typeof(Effect));

        Assert.True(registered.ContainsKey(typeof(MayPay)), "MayPay has no [JsonDerivedType] on Effect.");
        Assert.Equal(nameof(MayPay), registered[typeof(MayPay)]);
    }

    [Fact]
    public void The_two_mana_triggers_are_registered_under_Trigger()
    {
        var registered = CardFileSerializer.RegisteredNames(typeof(Trigger));

        Assert.True(registered.ContainsKey(typeof(TappedForMana)), "TappedForMana has no [JsonDerivedType] on Trigger.");
        Assert.True(registered.ContainsKey(typeof(ManaSpent)), "ManaSpent has no [JsonDerivedType] on Trigger.");
        Assert.Equal(nameof(TappedForMana), registered[typeof(TappedForMana)]);
        Assert.Equal(nameof(ManaSpent), registered[typeof(ManaSpent)]);
    }

    [Fact]
    public void CreatedTokenThisTurn_is_registered_under_Condition()
    {
        var registered = CardFileSerializer.RegisteredNames(typeof(Condition));

        Assert.True(registered.ContainsKey(typeof(CreatedTokenThisTurn)), "CreatedTokenThisTurn has no [JsonDerivedType] on Condition.");
        Assert.Equal(nameof(CreatedTokenThisTurn), registered[typeof(CreatedTokenThisTurn)]);
    }

    [Fact]
    public void A_may_pay_round_trips_with_both_halves()
    {
        var back = V6RoundTrip(V6DefinitionWith(V6MayPay()));

        var pay = Assert.IsType<MayPay>(Assert.IsType<SpellAbility>(back.Faces[0].AbilityList[0]).Do);
        Assert.Equal(new ManaCost("{4}"), pay.Paid);
        Assert.Equal(new Draw(new Fixed(2)), pay.Then);
        Assert.Equal(new LoseLife(new Fixed(2)), pay.Else);
        Assert.Null(pay.Who);
    }

    [Fact]
    public void The_tapped_for_mana_trigger_and_the_mana_key_round_trip()
    {
        var back = V6RoundTrip(V6DefinitionWith(V6TappedForChosenColor()));

        var ability = Assert.IsType<TriggeredAbility>(back.Faces[0].AbilityList[0]);
        var tapped = Assert.IsType<TappedForMana>(ability.When);
        Assert.Equal("chosen", tapped.OfChosenColorKey);
        Assert.Null(tapped.OfColor);

        var steps = Assert.IsType<Sequence>(ability.Do).Steps;
        Assert.Equal("extra", Assert.IsType<AddMana>(steps[1]).Key);
        var delayed = Assert.IsType<DelayedTrigger>(steps[2]);
        var spent = Assert.IsType<ManaSpent>(delayed.When);
        Assert.Equal("extra", spent.ManaKey);
        Assert.Equal(true, spent.OnSpell?.Spell);
    }

    [Fact]
    public void The_token_condition_and_the_basic_land_type_filter_round_trip()
    {
        var back = V6RoundTrip(V6DefinitionWith(V6CreatedTokenAndBasicLandType()));

        var ability = Assert.IsType<TriggeredAbility>(back.Faces[0].AbilityList[0]);
        Assert.IsType<CreatedTokenThisTurn>(ability.If);
        Assert.Equal(true, Assert.IsType<Search>(ability.Do).For.HasBasicLandType);
    }

    [Fact]
    public void An_activation_restricted_to_mana_from_a_source_round_trips()
    {
        var back = V6RoundTrip(V6DefinitionWith(V6PaidOnlyFromArtifacts()));

        var ability = Assert.IsType<ActivatedAbility>(back.Faces[0].AbilityList[0]);
        Assert.Equal(CardType.Artifact, ability.PayOnlyFrom?.Types);
        Assert.Null(ability.PayOnlyWith);
    }

    [Fact]
    public void A_file_carrying_every_wave8_addition_round_trips_whole()
    {
        var file = V6FileWith(V6MayPay(), V6TappedForChosenColor(), V6CreatedTokenAndBasicLandType(), V6PaidOnlyFromArtifacts());

        var json = CardFileSerializer.Write(file);
        var back = CardFileSerializer.Read(json);

        Assert.Equal(json, CardFileSerializer.Write(back));
        Assert.Equal(4, back.Definition.Faces[0].AbilityList.Count);
    }

    [Fact]
    public void Every_wave8_addition_lints_clean()
    {
        var report = V6Lint(V6MayPay(), V6TappedForChosenColor(), V6CreatedTokenAndBasicLandType(), V6PaidOnlyFromArtifacts());

        Assert.Empty(report.Findings);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void A_may_pay_offering_a_loyalty_cost_is_an_error()
    {
        // CR 606.2: a loyalty cost is only paid to activate a loyalty ability, never on resolution.
        var report = V6Lint(new SpellAbility(new MayPay(new LoyaltyCost(-2), new Draw(new Fixed(1)))));

        Assert.Contains(report.Findings, f => f.Code == "L014" && f.Severity == LintSeverity.Error && f.Message.Contains("LoyaltyCost", StringComparison.Ordinal));
    }

    [Fact]
    public void A_may_pay_whose_cost_asks_for_nothing_is_an_error()
    {
        var report = V6Lint(new SpellAbility(new MayPay(new Costs([]), new Draw(new Fixed(1)))));

        Assert.Contains(report.Findings, f => f.Code == "L014" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_may_pay_that_does_nothing_either_way_is_a_warning()
    {
        var report = V6Lint(new SpellAbility(new MayPay(new PayLife(new Fixed(1)), new DoNothing())));

        Assert.Contains(report.Findings, f => f.Code == "L014" && f.Severity == LintSeverity.Warning);
    }

    [Fact]
    public void A_tapped_for_mana_naming_two_colours_is_an_error()
    {
        var trigger = new TappedForMana(ObjectFilter.Any, OfColor: Color.Green, OfChosenColorKey: "chosen");
        var report = V6Lint(new TriggeredAbility(trigger, new Draw(new Fixed(1))));

        Assert.Contains(report.Findings, f => f.Code == "L015" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void An_ability_restricted_by_both_colour_and_source_is_an_error()
    {
        var ability = new ActivatedAbility([new ManaCost("{1}")], new Draw(new Fixed(1)), PayOnlyWith: Color.Red, PayOnlyFrom: new ObjectFilter(Types: CardType.Artifact));
        var report = V6Lint(ability);

        Assert.Contains(report.Findings, f => f.Code == "L016" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_pay_only_from_that_restricts_nothing_is_an_error()
    {
        var ability = new ActivatedAbility([new ManaCost("{1}")], new Draw(new Fixed(1)), PayOnlyFrom: new ObjectFilter());
        var report = V6Lint(ability);

        Assert.Contains(report.Findings, f => f.Code == "L016" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void The_shapes_the_pilot_reported_missing_that_already_existed_have_no_second_spelling()
    {
        // Each of these was on V6's list and is not added, because the vocabulary already says it.
        // A second spelling of any of them costs translator agreement, which is what the list is for.
        var filter = typeof(ObjectFilter).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("InCombat", filter);                                  // "attacking or blocking", CR 506.2
        Assert.DoesNotContain("AttackingOrBlocking", filter);

        Assert.NotNull(typeof(Search).GetProperty("Zones"));                  // a search across several zones, CR 701.23a
        Assert.NotNull(typeof(PayManaCost).GetProperty("Generic"));           // a counted generic cost, CR 118.8
        Assert.NotNull(typeof(Devotion).GetProperty("ChosenColorKey"));       // devotion to the chosen colour, CR 700.5
        Assert.NotNull(typeof(ManaProduction).GetProperty("ChosenColorKey")); // "one mana of that color", CR 106
        Assert.NotNull(typeof(AttackedThisTurn).GetProperty("Min"));          // "with two or more creatures", CR 506.2
        Assert.Null(typeof(Effect).Assembly.GetType("MtgEngine.Cards.AttackedWithCount"));
        Assert.Null(typeof(Effect).Assembly.GetType("MtgEngine.Cards.LifeTotalCantChange"));
        Assert.Null(typeof(Effect).Assembly.GetType("MtgEngine.Cards.PayGenericMana"));
    }

    [Fact]
    public void The_schema_carries_the_rule_text_the_translator_reads()
    {
        var defs = CardFileSerializer.DefinitionSchema()["$defs"]!;

        // Each phrase is in the doc comment and not in the [RuleRef] fallback, so a record that
        // lost its doc comment fails here rather than shipping a bare rule number to the model.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MayPay"] = "118.12a",
            ["TappedForMana"] = "106.12a",
            ["ManaSpent"] = "603.7a",
            ["CreatedTokenThisTurn"] = "111.1",
            ["ObjectFilter"] = "205.3i",
            ["ActivatedAbility"] = "106.6b",
        };

        foreach (var (name, phrase) in expected)
        {
            var description = defs[name]!["description"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(description), $"{name} has no description in the schema; the build's XML docs are how it gets one.");
            Assert.Contains(phrase, description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_new_records_are_in_the_generated_schema_under_their_bases()
    {
        var node = CardFileSerializer.DefinitionSchema();
        var defs = node["$defs"]!;

        Assert.Equal("MayPay", defs["MayPay"]!["properties"]!["kind"]!["const"]!.GetValue<string>());
        Assert.NotNull(defs["ObjectFilter"]!["properties"]!["hasBasicLandType"]);

        var effects = Names(node, "Effect");
        Assert.Contains("#/$defs/MayPay", effects, StringComparer.Ordinal);
        var triggers = Names(node, "Trigger");
        Assert.Contains("#/$defs/TappedForMana", triggers, StringComparer.Ordinal);
        Assert.Contains("#/$defs/ManaSpent", triggers, StringComparer.Ordinal);
        var conditions = Names(node, "Condition");
        Assert.Contains("#/$defs/CreatedTokenThisTurn", conditions, StringComparer.Ordinal);

        static List<string> Names(JsonNode node, string baseName) =>
            node["$defs"]![baseName]!["anyOf"]!.AsArray().Select(o => o!["$ref"]!.GetValue<string>()).ToList();
    }
}
