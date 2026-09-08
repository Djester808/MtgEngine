using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// Wave 28 (seam-requests/V17.md): every addition names itself in the later spellings, and each line
/// that exists to settle a choice between two legal-looking readings says which one is the spelling —
/// a worked-out token size against a SetPt, a keep-one-of-each edict against six sacrifices, an
/// additional cost on a permission against a waived one, a forbidden entry against a replacement, a
/// list of zones against a single zone, a record's cast zones against a filter's, storm against a
/// hand-written copy trigger, and a phased-out status against a zone.
/// </summary>
public sealed partial class TranslationPromptTests
{
    [Fact]
    public void Every_wave_twenty_eight_record_has_a_one_spelling_line()
    {
        var later = TranslationPrompt.LaterSpellings;
        foreach (var record in new[]
        {
            nameof(TokenSpec), nameof(SetPt), nameof(Sacrifice), nameof(MayPlayFromZone),
            nameof(CantEnterBattlefield), nameof(ObjectFilter), nameof(CantCastSpells),
            nameof(KeywordAbility), nameof(PhasedOutIs), nameof(PhaseOut),
        })
            Assert.Contains(record, later, StringComparison.Ordinal);

        foreach (var member in new[]
        {
            "\"powerAmount\"", "\"toughnessAmount\"", "\"keepOnePerPermanentType\": true",
            "\"oncePerTurn\": true", "\"additionalCost\"",
            "\"inAnyOf\": [\"Graveyard\", \"Library\"]", "\"castFrom\": [\"Graveyard\", \"Library\"]",
            "\"keyword\": \"Storm\"", "\"phasedOut\": true",
        })
            Assert.Contains(member, later, StringComparison.Ordinal);

        Assert.Contains(later, TranslationPrompt.SystemWithVocabulary(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_stays_inside_its_budget()
    {
        // The reference is the bulk of it; the budget moved to 200k after wave 28 (DECISIONS.md).
        Assert.True(TranslationPrompt.SystemWithVocabulary().Length < 200_000, $"prompt is {TranslationPrompt.SystemWithVocabulary().Length} characters");
    }
}
