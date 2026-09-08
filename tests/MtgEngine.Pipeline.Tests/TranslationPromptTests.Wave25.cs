using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// Wave 25 (seam-requests/V14.md): every addition names itself in the later spellings, and the lines
/// that exist to settle a choice between two legal-looking readings say which one is the spelling —
/// an opponent read existentially, a scaling attack tax inside its cost, and the Heal a regenerator
/// must not be given.
/// </summary>
public sealed partial class TranslationPromptTests
{
    [Fact]
    public void Every_wave_twenty_five_record_has_a_one_spelling_line()
    {
        var later = TranslationPrompt.LaterSpellings;
        foreach (var record in new[]
        {
            nameof(Destroy), nameof(TriggeringLifeGained), nameof(TriggeringLifeLost), nameof(IsModified),
            nameof(CantAttack), nameof(MayCastAsThoughFlash), nameof(CreateToken), nameof(AnOpponent),
            nameof(Regenerate), nameof(AtStep),
        })
            Assert.Contains(record, later, StringComparison.Ordinal);

        foreach (var member in new[] { "\"rememberAs\"", "{\"kind\":\"IsModified\"}", "\"unlessPays\"", "\"attackingWhom\"", "{\"kind\":\"AnOpponent\"}", "\"step\": \"Main\"" })
            Assert.Contains(member, later, StringComparison.Ordinal);

        Assert.Contains(later, TranslationPrompt.SystemWithVocabulary(), StringComparison.Ordinal);
    }
}
