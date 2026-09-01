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
            Defense = face.Defense,
            Faces = card.Faces,
            ImageUriNormal = card.ImageUriNormal,
            ImageUriSmall = card.ImageUriSmall,
            ImageUriArtCrop = card.ImageUriArtCrop,
        };
    }

    /// <summary>
    /// The definition for one specialized version, or the card itself when it has none.
    /// </summary>
    /// <remarks>
    /// The same trick <see cref="Definition"/> plays, on the other list. Index 0 is the base
    /// card, so unspecializing is this same call with a zero, and the whole list travels with the
    /// swapped-in definition — which is what lets a permanent specialize again into another
    /// colour, and lets it come back.
    /// <para>
    /// The mana value is the base's plus one because a specialized version's cost <em>is</em> the
    /// base's plus one coloured pip. That is not an assumption: it holds on all nineteen cards
    /// and all ninety-five versions, and it is the fact the loader reads to say which colour a
    /// version is in the first place.
    /// </para>
    /// </remarks>
    public static CardDefinition Specialized(CardDefinition card, int index)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (index < 0 || index >= card.Specializations.Count)
            return card;

        var version = card.Specializations[index];

        return new CardDefinition
        {
            OracleId = index == 0
                ? BaseId(card.OracleId)
                : BaseId(card.OracleId) + "$" + index.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            Name = version.Name,
            OracleText = version.OracleText,
            ManaCostRaw = version.ManaCostRaw,
            Cmc = index == 0 ? card.Cmc : card.Cmc + 1,
            CardTypes = version.CardTypes,
            Subtypes = version.Subtypes,
            Supertypes = version.Supertypes,
            Keywords = version.Keywords,
            Colors = version.Colors,

            // CR 903.4: a specialized version is a different card with different mana symbols on
            // it, but nothing in this engine asks a permanent for its commander identity, and the
            // base card is the one a deck was built around.
            ColorIdentity = card.ColorIdentity,
            Power = version.Power,
            Toughness = version.Toughness,
            Defense = version.Defense,
            Faces = card.Faces,
            Specializations = card.Specializations,
            ImageUriNormal = card.ImageUriNormal,
            ImageUriSmall = card.ImageUriSmall,
            ImageUriArtCrop = card.ImageUriArtCrop,
        };
    }

    /// <summary>
    /// Which specialized version a definition is, read back off the id suffix.
    /// </summary>
    /// <remarks>
    /// Zero for a base card and for anything with no specializations at all — the same shape as
    /// <see cref="FaceIndexOf"/>, and for the same reason: the swap has to be readable back off
    /// the definition, so that nothing needs a state field to remember it.
    /// </remarks>
    public static int SpecializationIndexOf(string oracleId)
    {
        ArgumentNullException.ThrowIfNull(oracleId);

        var at = oracleId.IndexOf('$', StringComparison.Ordinal);
        return at >= 0
            && int.TryParse(
                oracleId[(at + 1)..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var index)
            ? index
            : 0;
    }

    /// <summary>
    /// The base card behind a specialized one, or the card itself when it is not specialized.
    /// </summary>
    /// <remarks>
    /// CR 400.7: the object that leaves the battlefield is a new object elsewhere, and what is
    /// in the graveyard afterwards is the card that was cast — not the version a discarded card
    /// turned it into. Without this a dead Klement, Life Acolyte would be a card nobody put in
    /// their deck, under an oracle id no deck list contains.
    /// </remarks>
    public static CardDefinition Unspecialized(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        return SpecializationIndexOf(card.OracleId) == 0 ? card : Specialized(card, 0);
    }

    /// <summary>The oracle id with any face or specialization suffix taken off.</summary>
    private static string BaseId(string oracleId)
    {
        var at = oracleId.IndexOfAny(['#', '$']);
        return at < 0 ? oracleId : oracleId[..at];
    }

    /// <summary>
    /// Which face a definition is, read back off the id suffix <see cref="Definition"/> writes.
    /// </summary>
    /// <remarks>
    /// Zero for an ordinary card and for a front face. This exists for the one seam where a face
    /// definition crosses onto the battlefield as a card in its own right — a spell cast
    /// transformed (CR 712.11a) resolves carrying the back face's definition, and the permanent
    /// it becomes has to record which face it is showing or the day/night rules and a later
    /// transform would read it as its front.
    /// </remarks>
    public static int FaceIndexOf(string oracleId)
    {
        ArgumentNullException.ThrowIfNull(oracleId);

        var at = oracleId.IndexOf('#', StringComparison.Ordinal);
        return at >= 0
            && int.TryParse(
                oracleId[(at + 1)..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var index)
            ? index
            : 0;
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
