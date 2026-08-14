using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>
/// Format rules shared by every path that puts a card into a Commander deck.
/// </summary>
internal static class CommanderRules
{
    /// <summary>
    /// True only when Scryfall reports the card as <c>legal</c> in Commander.
    /// </summary>
    /// <remarks>
    /// Checking <c>!= "banned"</c> is not equivalent and was the bug this replaces.
    /// Scryfall reports four values, and <c>not_legal</c> is the common one: it covers
    /// every card never printed for the format -- Un-sets, Alchemy digital-only cards,
    /// and standalone Universes Beyond products such as the Marvel sets. Those are not
    /// banned, so a banned-only check waved them straight through into decks.
    /// </remarks>
    public static bool IsLegalInCommander(CardDefinition def) =>
        def.Legalities.TryGetValue("commander", out var legality)
        && string.Equals(legality, "legal", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Legendary creatures, plus the cards that explicitly say they may head a deck
    /// (planeswalker commanders, backgrounds' partners, Grist, and similar).
    /// </summary>
    public static bool IsCommanderEligible(CardDefinition d)
    {
        if (!IsLegalInCommander(d))
            return false;
        if (d.CardTypes.HasFlag(CardType.Token))
            return false;

        bool legendaryCreature =
            d.Supertypes.Contains("Legendary") && d.CardTypes.HasFlag(CardType.Creature);

        return legendaryCreature
            || (d.OracleText?.Contains("can be your commander", StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
