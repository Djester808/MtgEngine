using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// Wave 27 (seam-requests/V16.md): every addition names itself in the later spellings, and each line
/// that exists to settle a choice between two legal-looking readings says which one is the spelling —
/// a forbidden keyword against a predicate, the greatest damage against a total, deaths against a
/// graveyard, a running prevention against a trigger on one, a repeated symbol against a scaled
/// generic, a paid mode cost against a budget price, a preparation frame against an adventure, a
/// nameless copy against a nameless token, a shared-name group against a plain count, and a mana
/// rider against a tap replacement.
/// </summary>
public sealed partial class TranslationPromptTests
{
    [Fact]
    public void Every_wave_twenty_seven_record_has_a_one_spelling_line()
    {
        var later = TranslationPrompt.LaterSpellings;
        foreach (var record in new[]
        {
            nameof(ObjectFilter), nameof(GreatestDamageDealtThisTurn), nameof(DamageDealtThisTurn),
            nameof(CreaturesDiedThisTurn), nameof(PreventedDamage), nameof(TriggeringPreventedDamage),
            nameof(CantAttack), nameof(Mode), nameof(ChooseMode), nameof(CreateToken),
            nameof(GreatestSameNameCount), nameof(WouldAddMana), nameof(WouldTap),
        })
            Assert.Contains(record, later, StringComparison.Ordinal);

        foreach (var member in new[]
        {
            "\"notHas\": \"Flying\"", "\"matching\"", "\"perAttackingCreature\": true", "\"paidCost\"",
            "\"budget\"", "\"copyOf\"", "\"tappedFor\": true", "\"Prepare\"", "\"PrepareSpell\"",
        })
            Assert.Contains(member, later, StringComparison.Ordinal);

        Assert.Contains(later, TranslationPrompt.SystemWithVocabulary(), StringComparison.Ordinal);
    }
}
