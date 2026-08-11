using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Covers the check that keeps suggestion explanations tied to real rules text.
/// The model described Goblin Bombardment as converting Treasure tokens into damage;
/// it sacrifices creatures, and Treasures are artifacts.
/// </summary>
public class ReasonGroundingTests
{
    private const string BombardmentText =
        "Sacrifice a creature: Goblin Bombardment deals 1 damage to any target.";

    [Fact]
    public void QuoteIsGrounded_AcceptsAVerbatimSpan()
    {
        Assert.True(DeckSuggestionsService.QuoteIsGrounded(
            "Sacrifice a creature", DeckSuggestionsService.Normalize(BombardmentText)));
    }

    [Fact]
    public void QuoteIsGrounded_IgnoresCaseAndPunctuationDifferences()
    {
        Assert.True(DeckSuggestionsService.QuoteIsGrounded(
            "sacrifice a creature:  GOBLIN BOMBARDMENT deals 1 damage",
            DeckSuggestionsService.Normalize(BombardmentText)));
    }

    [Fact]
    public void QuoteIsGrounded_RejectsAnAbilityTheCardDoesNotHave()
    {
        Assert.False(DeckSuggestionsService.QuoteIsGrounded(
            "Sacrifice a Treasure: deals 1 damage",
            DeckSuggestionsService.Normalize(BombardmentText)));
    }

    [Fact]
    public void QuoteIsGrounded_RejectsAQuoteTooShortToProveAnything()
    {
        // "damage" appears in the text but citing it supports no particular claim.
        Assert.False(DeckSuggestionsService.QuoteIsGrounded(
            "damage", DeckSuggestionsService.Normalize(BombardmentText)));
        Assert.False(DeckSuggestionsService.QuoteIsGrounded(
            "1 damage", DeckSuggestionsService.Normalize(BombardmentText)));
    }

    /// <summary>
    /// Mana abilities are almost all symbols, so a character-length floor rejected every
    /// mana rock's only quotable line and pushed them all onto the fallback path.
    /// </summary>
    [Fact]
    public void QuoteIsGrounded_AcceptsAShortManaAbility()
    {
        var manaVault = DeckSuggestionsService.Normalize(
            "Mana Vault doesn't untap during your untap step.\n{T}: Add {C}{C}{C}.");

        Assert.True(DeckSuggestionsService.QuoteIsGrounded("{T}: Add {C}{C}{C}.", manaVault));
        Assert.True(DeckSuggestionsService.QuoteIsGrounded(
            "{T}: Add {C}{C}.", DeckSuggestionsService.Normalize("{T}: Add {C}{C}.")));
    }

    [Fact]
    public void QuoteIsGrounded_RejectsEmptyAndNull()
    {
        var src = DeckSuggestionsService.Normalize(BombardmentText);
        Assert.False(DeckSuggestionsService.QuoteIsGrounded(null, src));
        Assert.False(DeckSuggestionsService.QuoteIsGrounded("   ", src));
    }

    [Fact]
    public void Normalize_CollapsesWhitespaceAndDropsPunctuation()
    {
        Assert.Equal(
            "flying haste when this creature dies create fourteen treasure tokens",
            DeckSuggestionsService.Normalize(
                "Flying, haste\nWhen this creature dies, create fourteen Treasure tokens."));
    }

    [Fact]
    public void FallbackReason_UsesTheFirstLineWhenThereIsNoActivatedAbility()
    {
        var text = "Flying, haste.\nWhen this creature dies, create fourteen Treasure tokens.";
        Assert.Equal("Flying, haste.", DeckSuggestionsService.FallbackReason(text));
    }

    /// <summary>
    /// Mana Vault leads with its drawback; quoting that as the reason to run it reads as
    /// an argument against the card.
    /// </summary>
    [Fact]
    public void FallbackReason_PrefersAnActivatedAbilityOverALeadingDrawback()
    {
        var manaVault =
            "Mana Vault doesn't untap during your untap step.\n"
            + "{T}: Add {C}{C}{C}.\n"
            + "At the beginning of your upkeep, you may pay {4}. If you do, untap Mana Vault.";

        Assert.Equal("{T}: Add {C}{C}{C}.", DeckSuggestionsService.FallbackReason(manaVault));
    }

    [Fact]
    public void FallbackReason_TruncatesTextWithNoSentenceBreak()
    {
        var reason = DeckSuggestionsService.FallbackReason(new string('x', 400));
        Assert.True(reason.Length <= 110, $"was {reason.Length}");
        Assert.EndsWith("…", reason);
    }

    [Fact]
    public void FallbackReason_LeavesShortTextIntact()
    {
        Assert.Equal(BombardmentText, DeckSuggestionsService.FallbackReason(BombardmentText));
    }
}
