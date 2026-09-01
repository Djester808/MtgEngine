using System.Text.RegularExpressions;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Cards;

/// <summary>
/// Whether the engine already plays a card correctly without any card-specific code.
/// </summary>
/// <remarks>
/// Most cards in a real deck are not doing anything the rules engine does not already do. A
/// basic land, a vanilla creature, and a creature whose entire rules text is "Flying" are all
/// fully handled: the keyword is parsed onto the card and combat reads it. Requiring a hand
/// written definition for each of those would keep the playable pool at a couple of dozen cards
/// for no gain.
/// <para>
/// The line has to be drawn honestly, though, and the honest line is narrow: a card counts as
/// covered only when <b>every</b> line of its text is a keyword this engine actually implements.
/// A keyword that is parsed onto the card but never read — ward, protection — is worse than an
/// unimplemented card, because the game looks right and plays wrong. That is why
/// <see cref="Honoured"/> is a list of what the engine reads rather than the whole enum.
/// </para>
/// </remarks>
public static class CardCoverage
{
    /// <summary>
    /// The keyword abilities the engine reads and acts on.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>Enum.GetValues&lt;KeywordAbility&gt;()</c>. Ward (CR 702.21) needs a
    /// cost paid on targeting and protection (CR 702.16) touches damage, targeting, enchanting
    /// and blocking; neither is implemented, so a card with one is not covered and says so.
    /// </remarks>
    public static readonly IReadOnlySet<string> Honoured = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "flying",
        "reach",
        "first strike",
        "double strike",
        "trample",
        "deathtouch",
        "lifelink",
        "vigilance",
        "haste",
        "menace",
        "defender",
        "indestructible",
        "hexproof",
        "shroud",
        "flash",
    };

    /// <summary>
    /// Reminder text, which is never rules the engine has to implement (CR 207.2).
    /// </summary>
    /// <remarks>
    /// Bounded and timed, per the house rule for a pattern run over text this code did not
    /// author — the oracle text comes from the card database rather than from a player, but the
    /// rule is that no regex runs untimed over text from outside.
    /// </remarks>
    private static readonly Regex ReminderText = new(
        @"\([^)]{0,400}\)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Whether this card needs no definition to play correctly.
    /// </summary>
    public static bool IsFullyCovered(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        // A token is created by something, not played from a deck; whatever made it is what
        // needs implementing.
        if (card.CardTypes.HasFlag(CardType.Token))
            return false;

        var text = StripReminders(card.OracleText);
        if (string.IsNullOrWhiteSpace(text))
        {
            // Nothing written on it. A vanilla creature or an unmodified basic land does exactly
            // what the rules already do — but a noncreature, nonland permanent with no text does
            // nothing at all, which is a card the database got wrong rather than one to play.
            return card.CardTypes.HasFlag(CardType.Creature)
                || card.CardTypes.HasFlag(CardType.Land)
                || card.CardTypes.HasFlag(CardType.Artifact);
        }

        // Every remaining line has to be keywords the engine reads. Keywords are printed one per
        // line, comma-separated within a line.
        foreach (var line in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var word in line.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Honoured.Contains(word.Trim().TrimEnd('.')))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Which keywords a covered card actually uses, for a report on what the pool can play.
    /// </summary>
    public static IReadOnlyList<string> KeywordsOf(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        var text = StripReminders(card.OracleText);
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return
        [
            .. text
                .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim().TrimEnd('.'))
                .Where(Honoured.Contains),
        ];
    }

    private static string StripReminders(string? oracleText)
    {
        if (string.IsNullOrWhiteSpace(oracleText))
            return string.Empty;

        try
        {
            return ReminderText.Replace(oracleText, string.Empty).Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            // Treated as "not covered": a card whose text cannot be read in time is a card the
            // engine should not claim to play.
            return oracleText;
        }
    }
}
