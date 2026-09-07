using MtgEngine.Cards;

namespace MtgEngine.Rules.Faces;

/// <summary>
/// Which face of a card the rules read, and the verbs that turn a card over. Pure functions
/// over a <see cref="CardDefinition"/>: no state, no events, no card names. The layer system
/// asks <see cref="ActiveFaces"/> what an object's characteristics come from; the casting
/// system asks <see cref="CastableFaces"/> which faces a player may choose; the interpreter
/// asks <see cref="Transform"/>, <see cref="Convert"/>, <see cref="Flip"/> and
/// <see cref="Meld"/> for the keyword actions of CR 701.27, 701.28, 710 and 701.42.
/// </summary>
[RuleRef("712")]
public static class FaceRules
{
    /// <summary>The front face, CR 712.1: the face with <see cref="FaceRole.Main"/>, or index 0 when a definition does not say.</summary>
    [RuleRef("712.1")]
    public static int FrontFace(CardDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        for (var i = 0; i < definition.Faces.Count; i++)
            if (definition.Faces[i].Role == FaceRole.Main)
                return i;
        return 0;
    }

    /// <summary>The index of the first face with this role, or null when the card has none.</summary>
    public static int? FaceWith(CardDefinition definition, FaceRole role)
    {
        ArgumentNullException.ThrowIfNull(definition);
        for (var i = 0; i < definition.Faces.Count; i++)
            if (definition.Faces[i].Role == role)
                return i;
        return null;
    }

    /// <summary>
    /// The faces whose characteristics an object has right now. One face for almost everything;
    /// a split card outside the stack has both halves combined, CR 709.4, so this returns a list
    /// rather than an index. <paramref name="faceIndex"/> is the face that is up — the object's
    /// stored <c>FaceIndex</c> — which only matters in the zones the rules let it matter in.
    /// </summary>
    [RuleRef("712.8")]
    public static ImmutableList<int> ActiveFaces(CardDefinition definition, Zone zone, int faceIndex)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var front = FrontFace(definition);
        var up = faceIndex >= 0 && faceIndex < definition.Faces.Count ? faceIndex : front;
        return definition.Layout switch
        {
            // CR 709.4: in every zone except the stack a split card is its two halves combined;
            // CR 709.3b: on the stack only the half being cast exists.
            Layout.Split => zone == Zone.Stack ? [up] : [.. Enumerable.Range(0, definition.Faces.Count)],

            // CR 710.2: a flip card is its normal face everywhere but a flipped permanent.
            Layout.Flip => zone == Zone.Battlefield ? [up] : [front],

            // CR 715.4 and 720.4: an adventurer or omen card has its normal characteristics
            // everywhere except on the stack as an Adventure or an Omen.
            Layout.Adventure or Layout.Omen => zone == Zone.Stack ? [up] : [front],

            // CR 712.8b: a meld card on the stack has only its front face; CR 712.8g: the melded
            // permanent on the battlefield has only the combined back face.
            Layout.Meld => zone == Zone.Battlefield ? [up] : [front],

            // CR 712.8a: a double-faced card outside the battlefield and the stack has only its
            // front face; CR 712.8d–f: the face that is up, where the rules let a face be up.
            Layout.Transform or Layout.ModalDfc => zone is Zone.Battlefield or Zone.Stack ? [up] : [front],

            _ => [front],
        };
    }

    /// <summary>
    /// The single face the layer system reads. Every layout but a split card outside the stack
    /// has exactly one, CR 712.8; a split card's combined characteristics (CR 709.4) are the
    /// layer system's to fold, and this returns its first half so a caller that has no notion
    /// of combining still reads a real face.
    /// </summary>
    [RuleRef("712.8")]
    public static int ActiveFace(CardDefinition definition, Zone zone, int faceIndex) =>
        ActiveFaces(definition, zone, faceIndex)[0];

    /// <summary>Whether this object's characteristics are two faces combined, CR 709.4.</summary>
    [RuleRef("709.4")]
    public static bool IsCombined(CardDefinition definition, Zone zone) =>
        definition is not null && definition.Layout == Layout.Split && zone != Zone.Stack;

    /// <summary>
    /// The faces a player may choose between when casting or playing this card from
    /// <paramref name="zone"/>. CR 601.3b: the choice is made before the card is put on the
    /// stack. <paramref name="onAdventure"/> is CR 715.3d's exiled adventurer, which may be
    /// played from exile but not as its Adventure again; CR 720.3d shuffles an Omen away
    /// instead, so no such state exists for one.
    /// </summary>
    [RuleRef("601.3b")]
    public static ImmutableList<CastableFace> CastableFaces(CardDefinition definition, Zone zone, bool onAdventure = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (zone is Zone.Stack or Zone.Battlefield)
            return [];

        var front = FrontFace(definition);
        var normal = Castable(definition, front, "601.3b");
        return definition.Layout switch
        {
            // CR 709.2 and 709.3: both halves are castable, and the player chooses which.
            Layout.Split => [.. Enumerable.Range(0, definition.Faces.Count).Select(i => Castable(definition, i, "709.3"))],

            // CR 715.3 and 720.3: normally, or as the Adventure or Omen half.
            Layout.Adventure or Layout.Omen when !onAdventure =>
                [normal, .. Alternative(definition).Select(i => Castable(definition, i, definition.Layout == Layout.Adventure ? "715.3" : "720.3"))],

            // CR 715.3d: the card exiled on an adventure may be played, but not as an Adventure.
            Layout.Adventure or Layout.Omen => [Castable(definition, front, "715.3d")],

            // CR 712.11b: the player casting a modal double-faced card chooses which face.
            // CR 712.12: a land face is played, not cast; the caller reads IsLandFace and routes it.
            Layout.ModalDfc => [.. Enumerable.Range(0, definition.Faces.Count).Select(i => Castable(definition, i, "712.11b"))],

            // CR 712.11: everything else is cast with its front face up. An effect that lets a
            // card be cast "transformed" (CR 712.11a) adds its face; it is not a base option.
            _ => [normal],
        };
    }

    private static IEnumerable<int> Alternative(CardDefinition definition)
    {
        for (var i = 0; i < definition.Faces.Count; i++)
            if (definition.Faces[i].Role is FaceRole.Adventure or FaceRole.Omen)
                yield return i;
    }

    private static CastableFace Castable(CardDefinition definition, int index, string rule)
    {
        var face = definition.Faces[index];
        return new CastableFace(index, face.Role, face.Types.Types.HasFlag(CardType.Land), face.Role is FaceRole.Adventure or FaceRole.Omen, rule);
    }

    /// <summary>
    /// Transform a permanent, CR 701.27a: turn it over so its other face is up. Nothing happens
    /// when the permanent is not a double-faced card (701.27c), when it is a meld card (712.4c),
    /// or when the face it would turn into is an instant or sorcery face (701.27d, 712.10).
    /// </summary>
    [RuleRef("701.27")]
    public static TurnedFace Transform(CardDefinition definition, int faceIndex)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Layout is not (Layout.Transform or Layout.ModalDfc))
            return TurnedFace.Nothing(faceIndex, definition.Layout == Layout.Meld ? "712.4c" : "701.27c");
        if (definition.Faces.Count < 2)
            return TurnedFace.Nothing(faceIndex, "701.27c");

        var other = faceIndex == FrontFace(definition) ? Back(definition) : FrontFace(definition);
        // CR 712.10: an instant or sorcery face is never turned up on the battlefield.
        var types = definition.Faces[other].Types.Types;
        if (types.HasFlag(CardType.Instant) || types.HasFlag(CardType.Sorcery))
            return TurnedFace.Nothing(faceIndex, "712.10");
        return new TurnedFace(true, other, "701.27a");
    }

    /// <summary>Convert a permanent, CR 701.28a: the same game action as transform under a different word, and it follows 701.27a–f.</summary>
    [RuleRef("701.28")]
    public static TurnedFace Convert(CardDefinition definition, int faceIndex) => Transform(definition, faceIndex);

    /// <summary>
    /// Flip a permanent, CR 710: the alternative characteristics apply once it is flipped, and
    /// CR 710.4 makes it one-way — a flipped permanent never becomes unflipped.
    /// </summary>
    [RuleRef("710")]
    public static TurnedFace Flip(CardDefinition definition, int faceIndex)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Layout != Layout.Flip)
            return TurnedFace.Nothing(faceIndex, "710.1");
        if (FaceWith(definition, FaceRole.Flipped) is not { } flipped)
            return TurnedFace.Nothing(faceIndex, "710.1");
        // CR 710.4: flipping is a one-way process.
        return faceIndex == flipped ? TurnedFace.Nothing(faceIndex, "710.4") : new TurnedFace(true, flipped, "710.2");
    }

    /// <summary>
    /// Meld two cards, CR 701.42a: they are put onto the battlefield with their back faces up and
    /// combined, as a single object represented by two cards. CR 701.42b: only two cards of the
    /// same meld pair can be melded, which here means two meld-layout definitions whose combined
    /// back faces are the same face; CR 701.42c leaves anything else where it is.
    /// </summary>
    [RuleRef("701.42")]
    public static Melded Meld(CardDefinition first, CardDefinition second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.Layout != Layout.Meld || second.Layout != Layout.Meld || ReferenceEquals(first, second))
            return Melded.No("701.42b");
        if (FaceWith(first, FaceRole.MeldResult) is not { } left || FaceWith(second, FaceRole.MeldResult) is not { } right)
            return Melded.No("701.42b");
        // CR 701.42b: the pair is identified by the combined back face the two cards share.
        if (!string.Equals(first.Faces[left].Name, second.Faces[right].Name, StringComparison.Ordinal))
            return Melded.No("701.42b");
        return new Melded(true, first, left, "701.42a");
    }

    /// <summary>
    /// Where a spell goes as it finishes resolving, when the face it was cast as says somewhere
    /// other than the graveyard: CR 715.3d exiles an Adventure and lets its owner play it later,
    /// CR 720.3d shuffles an Omen into its owner's library.
    /// </summary>
    [RuleRef("715.3d")]
    public static Resolution ResolvesTo(CardDefinition definition, int faceIndex)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (faceIndex < 0 || faceIndex >= definition.Faces.Count)
            return Resolution.Graveyard;
        return definition.Faces[faceIndex].Role switch
        {
            FaceRole.Adventure => new Resolution(Zone.Exile, true, "715.3d"),
            FaceRole.Omen => new Resolution(Zone.Library, false, "720.3d"),
            _ => Resolution.Graveyard,
        };
    }

    /// <summary>
    /// The face a face-down spell or permanent shows, CR 708.2a: a 2/2 creature with no name, no
    /// subtypes, no text and no mana cost, and colorless because it has neither mana cost nor
    /// color indicator. Manifest (CR 701.40a) and morph (CR 702.37b) both produce this face; an
    /// ability that lists characteristics of its own replaces it, CR 708.2.
    /// </summary>
    [RuleRef("708.2a")]
    public static Face FaceDown { get; } = new(
        Name: string.Empty,
        Role: FaceRole.Main,
        Types: new TypeLine(CardType.Creature),
        Power: new PrintedStat("2"),
        Toughness: new PrintedStat("2"));

    private static int Back(CardDefinition definition)
    {
        var front = FrontFace(definition);
        for (var i = 0; i < definition.Faces.Count; i++)
            if (i != front)
                return i;
        return front;
    }
}

/// <summary>One face a player may choose when casting or playing a card, with the rule that offers it.</summary>
[RuleRef("601.3b")]
public sealed record CastableFace(int FaceIndex, FaceRole Role, bool IsLandFace, bool IsAlternative, string Rule);

/// <summary>The result of transforming, converting or flipping: whether the card turned over, and the rule that says so.</summary>
[RuleRef("701.27")]
public sealed record TurnedFace(bool Turned, int FaceIndex, string Rule)
{
    public static TurnedFace Nothing(int faceIndex, string rule) => new(false, faceIndex, rule);
}

/// <summary>The result of a meld, CR 701.42: which definition and face the melded permanent reads.</summary>
[RuleRef("701.42")]
public sealed record Melded(bool Success, CardDefinition? Combined, int FaceIndex, string Rule)
{
    public static Melded No(string rule) => new(false, null, 0, rule);
}

/// <summary>Where a resolving spell goes, and whether its owner may play it from there afterwards.</summary>
[RuleRef("715.3d")]
public sealed record Resolution(Zone Zone, bool MayCastLater, string Rule)
{
    /// <summary>The default, CR 608.2m: an instant or sorcery spell is put into its owner's graveyard.</summary>
    public static Resolution Graveyard { get; } = new(Zone.Graveyard, false, "608.2m");
}
