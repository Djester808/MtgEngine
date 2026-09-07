using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// What the parser does with an answer that is not quite a card definition. The strict half is the
/// point: a member no record has is an error naming it, because a translator found that a marker
/// dropped into a filter slot used to read as an empty filter and lint clean.
/// </summary>
public sealed class TranslationPromptTests
{
    private const string OracleId = "00000000-0000-0000-0000-0000000000d1";

    private static CardDefinition Definition(Effect effect) => new(
        OracleId: OracleId,
        Name: "Prompt Scribe",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Prompt Scribe",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Instant),
                ManaCost: "{U}",
                Abilities: [new SpellAbility(effect)],
                OracleText: "Prompt Scribe"),
        ]);

    [Fact]
    public void A_well_formed_answer_parses_even_inside_a_fence()
    {
        var json = CardFileSerializer.WriteDefinition(Definition(new Draw(new Fixed(1))));
        var (definition, error) = TranslationPrompt.Parse("```json\n" + json + "\n```");

        Assert.Null(error);
        Assert.Equal(OracleId, definition!.OracleId);
    }

    [Fact]
    public void A_misspelled_member_is_a_parse_error_naming_it()
    {
        var json = CardFileSerializer.WriteDefinition(Definition(new Draw(new Fixed(1))));
        var misspelled = json.Replace("\"n\": {", "\"nn\": {", StringComparison.Ordinal);
        Assert.NotEqual(json, misspelled);

        var (definition, error) = TranslationPrompt.Parse(misspelled);

        Assert.Null(definition);
        Assert.Contains("'nn'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_marker_dropped_into_a_filter_slot_is_refused_not_read_as_an_empty_filter()
    {
        // "Destroy each permanent" is what the answer used to say once the marker was dropped.
        var json = CardFileSerializer.WriteDefinition(Definition(new Destroy(new Each(ObjectFilter.Any))));
        var marked = json.Replace("\"filter\": {}", "\"filter\": { \"kind\": \"Unsupported\", \"reason\": \"no primitive\" }", StringComparison.Ordinal);
        Assert.NotEqual(json, marked);

        var (definition, error) = TranslationPrompt.Parse(marked);

        Assert.Null(definition);
        Assert.Contains("'kind'", error, StringComparison.Ordinal);
    }
}
