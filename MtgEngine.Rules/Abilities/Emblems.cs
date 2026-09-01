using System.Collections.Immutable;
using System.Reflection;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>
/// An emblem: an object in the command zone that is nothing but the abilities it was made with
/// (CR 114).
/// </summary>
/// <remarks>
/// CR 114.1 and 114.3 say what one is by saying what it is not — no card types, no mana cost, no
/// colour, usually no name — and CR 114.4 says the one thing it does: <em>its abilities function
/// in the command zone</em>. So an emblem needs no new zone, no new event and no new state field.
/// It is an object created into <see cref="Zone.Command"/> the way a dungeon is, carrying a
/// <see cref="CardDefinition"/> whose entire text is the ability the effect quoted, which
/// <see cref="CompiledPool"/> then compiles like any other card.
/// <para>
/// That reuse is the whole design, and it is the same trick <see cref="TokenCards.Granting"/>
/// made available a round earlier: the abilities a card grants something it creates have exactly
/// one place to live, which is a definition of that thing's own. An emblem is a granted ability
/// with the card taken away — no types, no power, and the command zone instead of the
/// battlefield.
/// </para>
/// <para>
/// <strong>Nothing here removes an emblem, and that is the rule rather than an omission.</strong>
/// CR 114 gives no way to destroy, exile or bounce one; a board wipe sweeps the battlefield and
/// an emblem is not on it. The state-based actions that clear the command zone are
/// <see cref="Engine.StateBasedActions"/>'s dungeon check and nothing else, so an emblem stays
/// for the game.
/// </para>
/// </remarks>
public static class Emblems
{
    /// <summary>
    /// What marks an object in the command zone as an emblem, the way a dungeon is marked.
    /// </summary>
    /// <remarks>
    /// An id prefix rather than a card type, because CR 114.5 is explicit that emblem is not a
    /// card type. A flag on <see cref="CardType"/> would make emblems findable by everything that
    /// sweeps for a type, which is the mistake this prefix avoids on both objects.
    /// </remarks>
    public const string OracleIdPrefix = "emblem:";

    /// <summary>The name every emblem carries, since CR 114.3 gives most of them none.</summary>
    /// <remarks>
    /// A name is needed all the same, and for a mechanical reason rather than a cosmetic one:
    /// the compiler turns a card's own name into <c>~</c> so that a self-reference reads
    /// (<c>CardCompiler.SelfNames</c>), and an emblem that says "this emblem deals 1 damage to
    /// you" has to have something for that word to become. <see cref="CardFor"/> rewrites the
    /// printed phrase to this name and the compiler does the rest, which is how the wording eight
    /// printed emblems use is read without teaching the shared self-reference vocabulary a word
    /// that is not a card type.
    /// </remarks>
    public const string EmblemName = "Emblem";

    /// <summary>Whether this card is the marker an emblem object carries (CR 114.1).</summary>
    public static bool IsEmblem(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        return card.OracleId.StartsWith(OracleIdPrefix, StringComparison.Ordinal);
    }

    /// <summary>Whether this object is an emblem in the command zone (CR 114.1, 114.4).</summary>
    public static bool IsEmblem(GameObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        return obj.Zone == Zone.Command && IsEmblem(obj.Card);
    }

    /// <summary>
    /// The card-shaped marker for an emblem with this ability text (CR 114.3).
    /// </summary>
    /// <remarks>
    /// Keyed on a stable hash of the text for the reason <see cref="TokenCards.Granting"/> is:
    /// the id reaches the event log, and a log replayed in a later process has to name the same
    /// object. Two emblems with the same wording are the same definition, which is right — they
    /// do the same thing, and <see cref="CompiledPool"/> refuses two cards sharing an id with
    /// different text.
    /// <para>
    /// The printed "this emblem" is rewritten to the emblem's name here, before anything reads
    /// it, so that the compiler's ordinary self-reference handling turns it into <c>~</c>. Doing
    /// it here rather than in <c>SelfReferenceTypeNames</c> keeps the rewrite off every other
    /// card in the corpus: that list is applied to printed card text, and a planeswalker's line
    /// mentioning "this emblem" is a quotation, not a reference to itself.
    /// </para>
    /// </remarks>
    public static CardDefinition CardFor(string abilityText)
    {
        ArgumentNullException.ThrowIfNull(abilityText);

        var text = abilityText
            .Replace("This emblem", EmblemName, StringComparison.Ordinal)
            .Replace("this emblem", EmblemName, StringComparison.Ordinal);

        return new CardDefinition
        {
            OracleId = OracleIdPrefix + EffectPhrase.StableHash(text),
            Name = EmblemName,

            // CR 114.3: no types, no mana cost, no colour. None rather than Other, which is what
            // a dungeon carries - a dungeon *is* a card type (CR 309.2) and an emblem is not
            // (CR 114.5).
            CardTypes = CardType.None,
            OracleText = text,
        };
    }

    /// <summary>
    /// The emblem's triggered abilities, moved to the zone they function in (CR 114.4).
    /// </summary>
    /// <remarks>
    /// The compiler writes <see cref="Zone.Battlefield"/> onto every trigger it reads, because
    /// that is where all but a handful of printed abilities work. An emblem's abilities are the
    /// handful: they are compiled from the quoted text exactly as a permanent's would be, and
    /// then re-keyed here so <c>Game.Consider</c>'s zone test lets them watch.
    /// <para>
    /// Re-keyed rather than compiled differently, because the text is a permanent's text — "at
    /// the beginning of your end step, create three 1/1 white Cat creature tokens with lifelink"
    /// is the same sentence on a card and on an emblem, and only the zone its source sits in
    /// differs.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<TriggeredAbilityDefinition> TriggersOf(
        GameObject obj, IAbilitySource abilities)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(abilities);

        if (!IsEmblem(obj))
            return [];

        return [.. abilities.TriggersOf(obj.Card).Select(t => t with { FunctionsFrom = Zone.Command })];
    }

    /// <summary>
    /// The emblem's static abilities (CR 114.4), which apply from the command zone.
    /// </summary>
    /// <remarks>
    /// Returned rather than filtered, because a continuous effect carries no zone of its own —
    /// <c>Characteristics.Candidates</c> decides which sources it sweeps, and until this existed
    /// it swept the battlefield alone. "Creatures you control get +2/+2 and have flying" is an
    /// anthem whose source is not a permanent, which is a thing the layers had never been shown.
    /// </remarks>
    public static IReadOnlyList<ContinuousEffectDefinition> StaticsOf(
        GameObject obj, IAbilitySource abilities)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(abilities);

        return IsEmblem(obj) ? abilities.StaticsOf(obj.Card) : [];
    }

    /// <summary>
    /// Whether the engine can actually run this emblem's whole text (CR 114.4).
    /// </summary>
    /// <remarks>
    /// The fail-closed gate, and the reason it is not a hand-written list of what an emblem may
    /// carry. Two things have to be true: every line of the quoted ability compiled, and
    /// everything the compiler produced from it is something an emblem in the command zone
    /// actually reaches — which today is its triggered and its static abilities and nothing else.
    /// <para>
    /// An emblem whose text compiles to an activated ability, a replacement effect or a player
    /// quality would sit in the command zone reading perfectly and doing nothing at all, because
    /// none of those is gathered from that zone. That is the exact failure the compiler treats as
    /// worse than an unread line, so the line is refused instead and the card stays in the work
    /// queue with a name on it.
    /// </para>
    /// <para>
    /// <strong>The check is a comparison against an empty card rather than a list of fields.</strong>
    /// A list would be a fourth copy of "what a CompiledCard can hold", and this codebase has
    /// watched three of those go stale. Compared this way, a field added to
    /// <see cref="CompiledCard"/> tomorrow closes the gate by default: an emblem that starts
    /// filling it stops reading until somebody decides, deliberately, that an emblem may carry
    /// it. Fail-closed is the direction a drift has to fail in.
    /// </para>
    /// </remarks>
    public static bool Reads(CardDefinition emblem)
    {
        ArgumentNullException.ThrowIfNull(emblem);

        var compiled = CardCompiler.Compile(emblem);
        if (!compiled.IsComplete)
            return false;

        foreach (var property in Comparable)
        {
            if (!Equals(property.GetValue(compiled), property.GetValue(Blank)))
                return false;
        }

        return compiled.Triggers.Count > 0 || compiled.Statics.Count > 0;
    }

    /// <summary>What an emblem in the command zone is actually asked for, and nothing else.</summary>
    /// <remarks>
    /// <see cref="CompiledCard.Unhandled"/> is here because <see cref="Reads"/> has already asked
    /// it the only question worth asking, which is whether it is empty.
    /// </remarks>
    private static readonly ImmutableHashSet<string> Reachable =
    [
        nameof(CompiledCard.Name),
        nameof(CompiledCard.Triggers),
        nameof(CompiledCard.Statics),
        nameof(CompiledCard.Unhandled),
    ];

    /// <summary>An emblem with no text at all: the shape everything else is measured against.</summary>
    private static readonly CompiledCard Blank = CardCompiler.Compile(CardFor(string.Empty));

    /// <remarks>
    /// Get-only properties are dropped rather than named, because every one of them is a summary
    /// of the fields beside it — <see cref="CompiledCard.IsComplete"/> reads
    /// <see cref="CompiledCard.Unhandled"/>, <see cref="CompiledCard.HasAbilities"/> reads
    /// fifteen others — and comparing a derivation as well as its inputs only ever produces a
    /// second, less legible way to fail. What has an <c>init</c> accessor is a field the compiler
    /// filled in, and those are exactly what this has to see.
    /// </remarks>
    private static readonly ImmutableArray<PropertyInfo> Comparable =
    [
        .. typeof(CompiledCard)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0
                && p.SetMethod is not null
                && !Reachable.Contains(p.Name)),
    ];
}
