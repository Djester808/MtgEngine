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
    public void Every_wave_nineteen_record_has_a_one_spelling_line()
    {
        // A record the model can see with nothing telling it which of two readings to write is what
        // cost the pilot 105 cards to disagreement (seam-requests/V11.md), so each wave-19 addition
        // names itself in the later spellings and the whole block reaches the system text.
        var later = TranslationPrompt.LaterSpellings;
        foreach (var record in new[]
        {
            nameof(DrawnThisTurn), nameof(ZoneChange), nameof(DamageDoesNotReduceLifeBelow), nameof(AlternativeCost),
            nameof(MayBeginGameOnBattlefield), nameof(AttacksWithCreatures), nameof(AttackersAgainst),
            nameof(TriggeringDamage), nameof(DelayedTrigger), nameof(BecomesCopyOf),
        })
            Assert.Contains(record, later, StringComparison.Ordinal);

        Assert.Contains("\"notFrom\": \"Battlefield\"", later, StringComparison.Ordinal);
        Assert.Contains("\"storeAs\"", later, StringComparison.Ordinal);
        Assert.Contains("\"exceptThisAbility\": true", later, StringComparison.Ordinal);
        Assert.Contains(later, TranslationPrompt.SystemWithVocabulary(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_output_rules_name_the_bases_that_carry_kind_and_say_no_other_object_does()
    {
        // The bases with a "kind" are exactly the "... kinds" sections of the reference; the records
        // fifteen third-reading answers gave a "kind" to are named as plain objects.
        var listedByReference = VocabularyReference.Text().Split('\n')
            .Where(line => line.StartsWith("## ", StringComparison.Ordinal) && line.TrimEnd().EndsWith(" kinds", StringComparison.Ordinal))
            .Select(line => line[3..].TrimEnd()[..^" kinds".Length])
            .ToList();
        Assert.NotEmpty(listedByReference);

        var rule = TranslationPrompt.OutputRules;
        var kindLine = rule.Split('\n').Single(line => line.StartsWith("- Only", StringComparison.Ordinal));
        var namedByRule = kindLine[(kindLine.LastIndexOf(": ", StringComparison.Ordinal) + 2)..].TrimEnd('.', '\r').Split(", ");
        Assert.Equal(listedByReference.OrderBy(n => n, StringComparer.Ordinal), namedByRule.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(listedByReference.OrderBy(n => n, StringComparer.Ordinal), TranslationPrompt.DefinitionBases().Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(nameof(Provenance), rule, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(ScenarioStep), rule, StringComparison.Ordinal);
        foreach (var plain in new[] { nameof(CardDefinition), nameof(Face), nameof(TypeLine), nameof(ObjectFilter), nameof(PrintedStat), nameof(ManaProduction), nameof(Comparison), nameof(SearchDestination) })
            Assert.Contains(plain, rule, StringComparison.Ordinal);
        Assert.Contains("No other object has a \"kind\"", rule, StringComparison.Ordinal);
        Assert.Contains(rule, TranslationPrompt.SystemWithVocabulary(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_kind_on_a_face_is_a_rejected_answer_naming_the_member()
    {
        var json = CardFileSerializer.WriteDefinition(Definition(new Draw(new Fixed(1))));
        var kinded = json.Replace("\"role\": \"Main\"", "\"kind\": \"Face\", \"role\": \"Main\"", StringComparison.Ordinal);
        Assert.NotEqual(json, kinded);

        var (definition, error) = TranslationPrompt.Parse(kinded);

        Assert.Null(definition);
        Assert.Contains("'kind'", error, StringComparison.Ordinal);
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
