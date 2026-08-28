using MtgEngine.Domain.Models;

namespace MtgEngine.Rules.Cards;

/// <summary>
/// Building a card definition for one face of a card that has several (CR 712.8d).
/// </summary>
/// <remarks>
/// A permanent shows one face at a time and has that face's characteristics. Rather than teach
/// every reader of a <see cref="CardDefinition"/> that faces exist, the object's card is swapped
/// for the face's when it turns over — so the layers, the ability source, the legality checks and
/// the per-player view all keep reading one definition and all of them read the right one.
/// <para>
/// The face list travels with the swapped-in definition, which is what lets the permanent turn
/// back, and the oracle id carries the face's index so a pool keyed by oracle id can hold both
/// sets of abilities at once.
/// </para>
/// </remarks>
public static partial class CardFaces
{
    /// <summary>The definition for one face, or the card itself when it has no such face.</summary>
    public static CardDefinition Definition(CardDefinition card, int index)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (index < 0 || index >= card.Faces.Count)
            return card;

        var face = card.Faces[index];

        return new CardDefinition
        {
            // The base id, so turning back and forth cannot pile up suffixes.
            OracleId = BaseId(card.OracleId) + "#" + index.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            Name = face.Name,
            OracleText = face.OracleText,
            ManaCostRaw = face.ManaCostRaw,

            // CR 712.8e: a nonmodal double-faced permanent with its back face up has its mana
            // value calculated from the front face, which is where the cost is printed.
            Cmc = card.Cmc,
            CardTypes = face.CardTypes,
            Subtypes = face.Subtypes,
            Supertypes = face.Supertypes,
            Keywords = KeywordsOf(face),
            Colors = face.Colors,
            ColorIdentity = card.ColorIdentity,
            Power = face.Power,
            Toughness = face.Toughness,
            Faces = card.Faces,
            ImageUriNormal = card.ImageUriNormal,
            ImageUriSmall = card.ImageUriSmall,
            ImageUriArtCrop = card.ImageUriArtCrop,
        };
    }

    /// <summary>The oracle id with any face suffix taken off.</summary>
    private static string BaseId(string oracleId)
    {
        var at = oracleId.IndexOf('#', StringComparison.Ordinal);
        return at < 0 ? oracleId : oracleId[..at];
    }

    /// <summary>
    /// The keywords this face actually has, rather than every keyword on the card.
    /// </summary>
    /// <remarks>
    /// The bulk data lists keywords once for the whole card, so a transforming card hands both
    /// faces the union of both - and a werewolf's front face came out with <em>daybound and
    /// nightbound at once</em>. The day/night rules then found it showing the wrong face whatever
    /// the time was, turned it over, and found the same thing again: a permanent that flipped for
    /// ever and a game that would not settle.
    /// <para>
    /// Only that pair is resolved here, and only by looking for the word on the face itself.
    /// Daybound and nightbound are the one pair the rules make mutually exclusive (CR 702.145b),
    /// so this cannot be right for one face and wrong for the other - and narrowing it to them
    /// keeps every other keyword exactly as the data gave it, rather than trading a known bug for
    /// a guess about the rest.
    /// </para>
    /// </remarks>
    private static Domain.Enums.KeywordAbility KeywordsOf(Domain.Models.CardFace face)
    {
        var keywords = face.Keywords;

        if (!Says(face, "Daybound"))
            keywords &= ~Domain.Enums.KeywordAbility.Daybound;

        if (!Says(face, "Nightbound"))
            keywords &= ~Domain.Enums.KeywordAbility.Nightbound;

        return keywords;
    }

    /// <summary>Whether this face prints the given keyword as a line of its own.</summary>
    private static bool Says(Domain.Models.CardFace face, string keyword)
    {
        foreach (var line in face.OracleText.Split('\n'))
        {
            var bare = Reminder().Replace(line, string.Empty).Trim().TrimEnd('.');

            if (bare.Equals(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\([^)]*\)")]
    private static partial System.Text.RegularExpressions.Regex Reminder();
}
