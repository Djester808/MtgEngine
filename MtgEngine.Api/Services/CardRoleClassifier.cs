using System.Text.RegularExpressions;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>Deck-building role a card fills, mirroring the composition quotas in the build prompt.</summary>
public enum CardRole
{
    Land,
    Ramp,
    Draw,
    Removal,
    /// <summary>Everything else: the strategy and synergy bulk of the deck.</summary>
    Other,
}

/// <summary>
/// Buckets cards by the job they do in a deck.
/// </summary>
/// <remarks>
/// A flat list of ~7,000 names gives the model no help hitting "36-38 lands, 8-10 ramp,
/// 8-10 draw, 8-10 interaction". Grouping by role turns the pool into something it can
/// draw proportionally from.
/// <para>
/// Classification is by function first, then card type, so Solemn Simulacrum lands in
/// Ramp rather than Other -- what the card *does* matters more here than what it is.
/// Each card gets exactly one role, so section sizes sum to the pool size.
/// </para>
/// These are text heuristics, not a rules engine. They are deliberately loose: a card in
/// a slightly wrong section is a much smaller problem than a card missing from the pool,
/// and the model can still read the name and judge for itself.
/// </remarks>
internal static class CardRoleClassifier
{
    // "Add {B}", "adds {C}{C}" -- mana production.
    private static readonly Regex AddsMana = new(@"\badds?\s+\{", RegexOptions.Compiled);

    // "Search your library for a basic land card" / "...for a Forest card".
    private static readonly Regex LandFetch = new(
        @"search your library for (a|up to \w+)[^.]*\bland\b",
        RegexOptions.Compiled);

    // "Draw a card", "draw two cards", "draws three cards".
    private static readonly Regex DrawsCards = new(@"\bdraws?\s+(a|one|two|three|four|five|\w+|X)\s+cards?\b",
        RegexOptions.Compiled);

    private static readonly Regex Removal = new(
        @"\b(destroy target|destroy all|destroy each|exile target|exile all|exile each" +
        @"|deals? \d+ damage to target|deals? X damage to target" +
        @"|target creature gets -|target player sacrifices|each (opponent|player) sacrifices" +
        @"|return target (creature|permanent|nonland permanent) to its owner's hand" +
        @"|counter target)\b",
        RegexOptions.Compiled);

    public static CardRole Classify(CardDefinition def)
    {
        // Lands are unambiguous and dominate the quota, so settle them first.
        if (def.CardTypes.HasFlag(CardType.Land))
            return CardRole.Land;

        var text = def.OracleText?.ToLowerInvariant() ?? string.Empty;
        if (text.Length == 0)
            return CardRole.Other;

        // Ramp before draw: a card that ramps and cantrips is used for the mana.
        if (AddsMana.IsMatch(text) || LandFetch.IsMatch(text))
            return CardRole.Ramp;

        // Removal before draw: "destroy target creature, then draw a card" is removal.
        if (Removal.IsMatch(text))
            return CardRole.Removal;

        if (DrawsCards.IsMatch(text))
            return CardRole.Draw;

        return CardRole.Other;
    }

    /// <summary>Heading used for this role in the prompt.</summary>
    public static string Label(CardRole role) => role switch
    {
        CardRole.Land => "LANDS",
        CardRole.Ramp => "RAMP / MANA",
        CardRole.Draw => "CARD DRAW",
        CardRole.Removal => "REMOVAL / INTERACTION",
        _ => "CREATURES, SYNERGY & EVERYTHING ELSE",
    };
}
