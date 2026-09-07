using System.Text.Json;
using System.Text.Json.Nodes;

namespace MtgEngine.Cards.Tests;

/// <summary>
/// The three primitives E12's keyword expansions stopped on (seam-requests/E12.md): a filter that
/// can say "this object" (CR 109.5, 603.2), the "discard this card" cost cycling asks for
/// (CR 702.29a), and the all-creature-types flag changeling needs (CR 702.72a, 205.3d). Each has to
/// be in the name table, in the schema the translator is constrained to, and lint clean, or a
/// translated card carrying it is lost silently.
/// </summary>
public sealed partial class VocabularyAdditionsTests
{
    // "Whenever this creature attacks, draw a card." — the shape undying, persist, afflict and
    // mentor all need, and the shape every "Whenever this creature…" card is translated into.
    private static TriggeredAbility WhenThisAttacks() =>
        new(new Attacks(new ObjectFilter(Types: CardType.Creature, Also: new IsThisObject())), new Draw(new Fixed(1)));

    // CR 702.29a: "[Cost], Discard this card: Draw a card."
    private static ActivatedAbility Cycling() =>
        new([new ManaCost("{2}"), new DiscardSelfCost()], new Draw(new Fixed(1)), Where: Zone.Hand);

    // CR 702.72a: changeling is a characteristic-defining ability, applied in layer 4.
    private static StaticAbility Changeling() =>
        new(new AddTypes(AllCreatureTypes: true), To: new Self(), CharacteristicDefining: true);

    private static CardDefinition DefinitionWith(params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-0000000000a1",
        Name: "Test Addition",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Test Addition",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Creature, Subtypes: ["Shapeshifter"]),
                ManaCost: "{1}{U}",
                Abilities: abilities,
                OracleText: "Test text.")
        ]);

    private static CardFile FileWith(params Ability[] abilities)
    {
        var definition = DefinitionWith(abilities);
        return new CardFile(
            OracleId: definition.OracleId,
            Name: definition.Name,
            OracleHash: OracleHashing.Of(definition.Faces.Select(f => f.OracleText)),
            IrVersion: IrVersion.Current,
            Definition: definition,
            Status: ImplementationStatus.Translated,
            Source: new TemplateProvenance("test"));
    }

    private static CardDefinition RoundTrip(CardDefinition definition) =>
        CardFileSerializer.ReadDefinition(CardFileSerializer.WriteDefinition(definition));

    [Fact]
    public void IsThisObject_is_registered_under_Predicate()
    {
        var registered = CardFileSerializer.RegisteredNames(typeof(Predicate));

        Assert.True(registered.ContainsKey(typeof(IsThisObject)), "IsThisObject has no [JsonDerivedType] on Predicate.");
        Assert.Equal(nameof(IsThisObject), registered[typeof(IsThisObject)]);
    }

    [Fact]
    public void DiscardSelfCost_is_registered_under_Cost()
    {
        var registered = CardFileSerializer.RegisteredNames(typeof(Cost));

        Assert.True(registered.ContainsKey(typeof(DiscardSelfCost)), "DiscardSelfCost has no [JsonDerivedType] on Cost.");
        Assert.Equal(nameof(DiscardSelfCost), registered[typeof(DiscardSelfCost)]);
    }

    [Fact]
    public void A_filter_saying_this_object_round_trips_with_its_kind()
    {
        var json = CardFileSerializer.WriteDefinition(DefinitionWith(WhenThisAttacks()));

        Assert.Contains("\"IsThisObject\"", json, StringComparison.Ordinal);
        var node = JsonNode.Parse(json)!;
        Assert.Equal(
            "IsThisObject",
            node["faces"]![0]!["abilities"]![0]!["when"]!["who"]!["also"]!["kind"]!.GetValue<string>());

        var back = CardFileSerializer.ReadDefinition(json);
        var trigger = Assert.IsType<Attacks>(Assert.IsType<TriggeredAbility>(back.Faces[0].AbilityList[0]).When);
        Assert.IsType<IsThisObject>(trigger.Who.Also);
        Assert.Equal(json, CardFileSerializer.WriteDefinition(back));
    }

    [Fact]
    public void Discard_this_card_round_trips_with_its_kind_and_is_not_discard_a_card()
    {
        var json = CardFileSerializer.WriteDefinition(DefinitionWith(Cycling()));

        Assert.Contains("\"DiscardSelfCost\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"DiscardCost\"", json, StringComparison.Ordinal);

        var back = CardFileSerializer.ReadDefinition(json);
        var ability = Assert.IsType<ActivatedAbility>(back.Faces[0].AbilityList[0]);
        Assert.IsType<DiscardSelfCost>(ability.Costs[1]);
        Assert.Equal(json, CardFileSerializer.WriteDefinition(back));
    }

    [Fact]
    public void AddTypes_carries_all_creature_types_through_a_round_trip()
    {
        var json = CardFileSerializer.WriteDefinition(DefinitionWith(Changeling()));

        Assert.Contains("\"allCreatureTypes\": true", json, StringComparison.Ordinal);

        var back = RoundTrip(DefinitionWith(Changeling()));
        var add = Assert.IsType<AddTypes>(Assert.IsType<StaticAbility>(back.Faces[0].AbilityList[0]).Effect);
        Assert.True(add.AllCreatureTypes);
        Assert.Null(add.Subtypes); // never a list of creature types: that would be card data in the engine
        Assert.Equal(json, CardFileSerializer.WriteDefinition(back));
    }

    [Fact]
    public void AddTypes_still_defaults_to_not_every_creature_type()
    {
        // The flag is additive; the spelling that was already in card files means what it meant.
        Assert.False(new AddTypes(CardType.Creature).AllCreatureTypes);
        var back = RoundTrip(DefinitionWith(new StaticAbility(new AddTypes(CardType.Creature), To: new Self())));
        Assert.False(Assert.IsType<AddTypes>(Assert.IsType<StaticAbility>(back.Faces[0].AbilityList[0]).Effect).AllCreatureTypes);
    }

    [Fact]
    public void A_definition_written_before_the_flag_existed_still_reads()
    {
        // IrVersion is not bumped: these are additions, so an old file deserialises unchanged.
        const string json = """
            {"kind": "AddTypes", "types": ["Creature"], "subtypes": ["Goblin"]}
            """;
        var effect = Assert.IsType<AddTypes>(JsonSerializer.Deserialize<ContinuousEffect>(json, CardFileSerializer.Options));

        Assert.False(effect.AllCreatureTypes);
        Assert.Equal(CardType.Creature, effect.Types);
    }

    [Fact]
    public void The_definition_schema_describes_all_three_additions()
    {
        var node = CardFileSerializer.DefinitionSchema();
        var defs = node["$defs"]!.AsObject();

        Assert.Equal("IsThisObject", defs["IsThisObject"]!["properties"]!["kind"]!["const"]!.GetValue<string>());
        Assert.Equal("DiscardSelfCost", defs["DiscardSelfCost"]!["properties"]!["kind"]!["const"]!.GetValue<string>());
        Assert.Equal("boolean", defs["AddTypes"]!["properties"]!["allCreatureTypes"]!["type"]!.GetValue<string>());

        var predicates = node["$defs"]!["Predicate"]!["anyOf"]!.AsArray().Select(o => o!["$ref"]!.GetValue<string>()).ToList();
        Assert.Contains("#/$defs/IsThisObject", predicates, StringComparer.Ordinal);
        var costs = node["$defs"]!["Cost"]!["anyOf"]!.AsArray().Select(o => o!["$ref"]!.GetValue<string>()).ToList();
        Assert.Contains("#/$defs/DiscardSelfCost", costs, StringComparer.Ordinal);
    }

    [Fact]
    public void The_schema_carries_the_rule_text_the_translator_reads()
    {
        // VocabularySchema turns the XML doc comment into the description; a record the model
        // cannot tell apart from its neighbour is a record it will misuse.
        var defs = CardFileSerializer.DefinitionSchema()["$defs"]!;

        // Each phrase is in the doc comment and not in the [RuleRef] fallback, so a record that
        // lost its doc comment fails here rather than shipping a bare rule number to the model.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["IsThisObject"] = "603.2",
            ["DiscardSelfCost"] = "cycling",
            ["AddTypes"] = "702.72a",
        };

        foreach (var (name, phrase) in expected)
        {
            var description = defs[name]!["description"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(description), $"{name} has no description in the schema; the build's XML docs are how it gets one.");
            Assert.Contains(phrase, description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void All_three_additions_lint_clean()
    {
        var report = Linter.Check(FileWith(WhenThisAttacks(), Cycling(), Changeling()));

        Assert.Empty(report.Findings);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void A_file_carrying_the_additions_round_trips_whole()
    {
        var file = FileWith(WhenThisAttacks(), Cycling(), Changeling());

        var json = CardFileSerializer.Write(file);
        var back = CardFileSerializer.Read(json);

        Assert.Equal(json, CardFileSerializer.Write(back));
        Assert.Equal(3, back.Definition.Faces[0].AbilityList.Count);
    }
}
