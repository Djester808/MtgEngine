using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// Wave 26 (seam-requests/V15.md): every addition names itself in the later spellings, and the lines
/// that exist to settle a choice between two legal-looking readings say which one is the spelling —
/// a public reveal against a private look, prevented damage against dealt damage, the triggering
/// spell's payment against this one's, distinctness against different mana values, and one placement
/// against two.
/// </summary>
public sealed partial class TranslationPromptTests
{
    [Fact]
    public void Every_wave_twenty_six_record_has_a_one_spelling_line()
    {
        var later = TranslationPrompt.LaterSpellings;
        foreach (var record in new[]
        {
            nameof(SacrificeCost), nameof(DamagePrevented), nameof(TriggeringPreventedDamage),
            nameof(DamageCantBePrevented), nameof(PlayWithTopCardRevealed), nameof(MayLookAtTopCard),
            nameof(WouldDraw), nameof(ManaSpentOnTriggering), nameof(ManaSpentOnThis),
            nameof(MoveCounters), nameof(Counter), nameof(SpellCast), nameof(AnyTarget),
        })
            Assert.Contains(record, later, StringComparison.Ordinal);

        foreach (var member in new[]
        {
            "\"additionalCosts\"", "\"thisWay\": true", "\"other\": true", "\"exceptFirstInDrawStep\": true",
            "\"distinct\"", "\"BottomRandomOrder\"", "\"anyNumber\": true", "\"where\": \"Top\"", "\"nth\": 2",
        })
            Assert.Contains(member, later, StringComparison.Ordinal);

        Assert.Contains(later, TranslationPrompt.SystemWithVocabulary(), StringComparison.Ordinal);
    }
}
