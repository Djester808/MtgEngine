using System.Text.Json;
using System.Text.Json.Nodes;

namespace MtgEngine.Cards.Tests;

/// <summary>
/// "Any target", CR 115.4, as one selector. Before it the vocabulary made a reading spell the
/// phrase across two fields of DealDamage that do not know about each other, so two models wrote
/// the same three words two ways and every "any target" card stayed Disputed
/// (seam-requests/P6b.md). The record only closes that if it is in the name table, in the schema
/// the translator is constrained to, and lint clean — a card carrying a record the serializer does
/// not know is lost silently.
/// </summary>
public sealed class AnyTargetTests
{
    // "Deals 3 damage to any target." — the whole phrase, and the only spelling of it.
    private static SpellAbility Bolt() => new(new DealDamage(new Fixed(3), new AnyTarget()));

    // "Deals 2 damage divided as you choose among one or two targets." — the counted form.
    private static SpellAbility Divided() =>
        new(new DealDamage(new Fixed(2), new AnyTarget(new Fixed(2), UpTo: true, Key: "hit")));

    private static CardDefinition DefinitionWith(params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-0000000000b4",
        Name: "Any Target Test",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Any Target Test",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Instant),
                ManaCost: "{R}",
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

    [Fact]
    public void AnyTarget_is_registered_under_Selector()
    {
        var registered = CardFileSerializer.RegisteredNames(typeof(Selector));

        Assert.True(registered.ContainsKey(typeof(AnyTarget)), "AnyTarget has no [JsonDerivedType] on Selector.");
        Assert.Equal(nameof(AnyTarget), registered[typeof(AnyTarget)]);
    }

    [Fact]
    public void Any_target_round_trips_with_its_kind_and_carries_no_player_half()
    {
        var json = CardFileSerializer.WriteDefinition(DefinitionWith(Bolt()));

        Assert.Contains("\"AnyTarget\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("toPlayer", json, StringComparison.Ordinal);
        var node = JsonNode.Parse(json)!;
        Assert.Equal("AnyTarget", node["faces"]![0]!["abilities"]![0]!["do"]!["to"]!["kind"]!.GetValue<string>());

        var back = CardFileSerializer.ReadDefinition(json);
        var damage = Assert.IsType<DealDamage>(Assert.IsType<SpellAbility>(back.Faces[0].AbilityList[0]).Do);
        Assert.IsType<AnyTarget>(damage.To);
        Assert.Null(damage.ToPlayer);
        Assert.Equal(json, CardFileSerializer.WriteDefinition(back));
    }

    [Fact]
    public void Any_target_carries_a_count_an_up_to_and_a_key_like_a_target_does()
    {
        var json = CardFileSerializer.WriteDefinition(DefinitionWith(Divided()));
        var back = CardFileSerializer.ReadDefinition(json);

        var any = Assert.IsType<AnyTarget>(Assert.IsType<DealDamage>(Assert.IsType<SpellAbility>(back.Faces[0].AbilityList[0]).Do).To);
        Assert.Equal(2, Assert.IsType<Fixed>(any.Count).N);
        Assert.True(any.UpTo);
        Assert.Equal("hit", any.Key);
        Assert.Equal(json, CardFileSerializer.WriteDefinition(back));
    }

    [Fact]
    public void A_count_of_one_is_the_default_so_it_need_not_be_written()
    {
        // The canonical pass strips a written Fixed(1) wherever the record's own default is null;
        // the record has to have that default for the strip to be honest.
        Assert.Null(new AnyTarget().Count);
        Assert.False(new AnyTarget().UpTo);
        Assert.Null(new AnyTarget().Key);
    }

    [Fact]
    public void A_definition_written_before_the_selector_existed_still_reads()
    {
        // IrVersion is not bumped: AnyTarget is an addition, so the two-field spelling still
        // deserialises and the canonical pass is what turns it into this one.
        const string json = """
            {"kind": "DealDamage", "n": {"kind": "Fixed", "n": 3},
             "to": {"kind": "Target", "filter": {"anyTypes": ["Creature", "Planeswalker", "Battle"]}},
             "toPlayer": {"kind": "TargetPlayer", "filter": {}}}
            """;
        var effect = Assert.IsType<DealDamage>(JsonSerializer.Deserialize<Effect>(json, CardFileSerializer.Options));

        Assert.IsType<Target>(effect.To);
        Assert.IsType<TargetPlayer>(effect.ToPlayer);
    }

    [Fact]
    public void The_definition_schema_names_AnyTarget_under_Selector()
    {
        var node = CardFileSerializer.DefinitionSchema();
        var defs = node["$defs"]!.AsObject();

        Assert.Equal("AnyTarget", defs["AnyTarget"]!["properties"]!["kind"]!["const"]!.GetValue<string>());

        var selectors = defs["Selector"]!["anyOf"]!.AsArray().Select(o => o!["$ref"]!.GetValue<string>()).ToList();
        Assert.Contains("#/$defs/AnyTarget", selectors, StringComparer.Ordinal);
    }

    [Fact]
    public void The_schema_carries_the_rule_the_translator_reads()
    {
        // The doc comment is how the model learns that the player half is not written beside it.
        var description = CardFileSerializer.DefinitionSchema()["$defs"]!["AnyTarget"]!["description"]?.GetValue<string>();

        Assert.False(string.IsNullOrWhiteSpace(description), "AnyTarget has no description in the schema; the build's XML docs are how it gets one.");
        Assert.Contains("115.4", description, StringComparison.Ordinal);
        Assert.Contains("TargetPlayers", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Any_target_lints_clean()
    {
        var report = Linter.Check(FileWith(Bolt(), Divided()));

        Assert.Empty(report.Findings);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void A_file_carrying_any_target_round_trips_whole()
    {
        var file = FileWith(Bolt());

        var json = CardFileSerializer.Write(file);
        var back = CardFileSerializer.Read(json);

        Assert.Equal(json, CardFileSerializer.Write(back));
        Assert.Single(back.Definition.Faces[0].AbilityList);
    }
}
