using System.Text.Json;
using System.Text.Json.Nodes;

namespace MtgEngine.Cards.Tests;

public sealed class SerializationTests
{
    [Fact]
    public void A_card_file_round_trips_through_json_unchanged()
    {
        var file = Fixtures.FileFor(Fixtures.Clamp(), scryfallKeywords: ["Equip"]);

        var json = CardFileSerializer.Write(file);
        var back = CardFileSerializer.Read(json);
        var again = CardFileSerializer.Write(back);

        Assert.Equal(json, again);
        Assert.Equal(file.OracleId, back.OracleId);
        Assert.Equal(3, back.Definition.Faces[0].AbilityList.Count);
        Assert.IsType<TriggeredAbility>(back.Definition.Faces[0].AbilityList[1]);
    }

    [Fact]
    public void Nested_polymorphic_records_keep_their_kinds()
    {
        var json = CardFileSerializer.WriteDefinition(Fixtures.Within());
        var back = CardFileSerializer.ReadDefinition(json);

        var spell = Assert.IsType<SpellAbility>(back.Faces[0].AbilityList[0]);
        var sequence = Assert.IsType<Sequence>(spell.Do);
        var destroy = Assert.IsType<Destroy>(sequence.Steps[0]);
        var target = Assert.IsType<Target>(destroy.What);
        Assert.Equal("it", target.Key);
        var create = Assert.IsType<CreateToken>(sequence.Steps[1]);
        var controller = Assert.IsType<ControllerOf>(create.Who);
        Assert.IsType<Remembered>(controller.Of);
    }

    [Fact]
    public void Flags_enums_are_arrays_of_names()
    {
        var json = CardFileSerializer.WriteDefinition(Fixtures.Within());
        var node = JsonNode.Parse(json)!;
        var token = node["faces"]![0]!["abilities"]![0]!["do"]!["steps"]![1]!["token"]!;

        Assert.Equal("[\"Green\"]", token["colors"]!.ToJsonString());
        Assert.Equal("[\"Creature\"]", token["types"]!.ToJsonString());
    }

    [Fact]
    public void The_discriminator_may_come_after_other_properties()
    {
        const string json = """{"n": {"n": 2, "kind": "Fixed"}, "kind": "Draw"}""";
        var effect = JsonSerializer.Deserialize<Effect>(json, CardFileSerializer.Options);
        var draw = Assert.IsType<Draw>(effect);
        Assert.Equal(2, Assert.IsType<Fixed>(draw.N).N);
    }

    [Fact]
    public void An_unknown_kind_is_an_error_not_a_base_record()
    {
        const string json = """{"kind": "Obliterate", "what": {"kind": "Self"}}""";
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Effect>(json, CardFileSerializer.Options));
    }

    [Fact]
    public void The_definition_schema_describes_effects_as_a_choice_of_kinds()
    {
        var schema = CardFileSerializer.DefinitionSchema().ToJsonString();
        var node = JsonNode.Parse(schema)!;

        Assert.Contains("\"kind\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"Draw\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"DealDamage\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"Equip\"", schema, StringComparison.Ordinal);
        Assert.NotNull(node["$defs"]?["CardDefinition"]?["properties"]?["faces"]);
        Assert.NotNull(node["$defs"]?["Effect"]?["anyOf"]);
        Assert.Equal("Draw", node["$defs"]?["Draw"]?["properties"]?["kind"]?["const"]?.GetValue<string>());
        // Every record reachable from the root has exactly one definition, and none is inlined twice.
        Assert.True(node["$defs"]!.AsObject().Count > 400, "the vocabulary should have hundreds of definitions");
    }

    [Fact]
    public void The_schema_writes_flags_enums_as_string_arrays()
    {
        var schema = CardFileSerializer.DefinitionSchema().ToJsonString();
        Assert.Contains("\"uniqueItems\":true", schema, StringComparison.Ordinal);
        Assert.Contains("\"Green\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Colorless\"", schema, StringComparison.Ordinal);
    }
}
