using System.Text.Json.Serialization;

namespace MtgEngine.Cards;

/// <summary>
/// The six shapes an ability can take, CR 113.3, plus the keyword form. Every line of a
/// card's text is one of these.
/// </summary>
[RuleRef("113.3")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(KeywordAbility), nameof(KeywordAbility))]
[JsonDerivedType(typeof(SpellAbility), nameof(SpellAbility))]
[JsonDerivedType(typeof(TriggeredAbility), nameof(TriggeredAbility))]
[JsonDerivedType(typeof(ActivatedAbility), nameof(ActivatedAbility))]
[JsonDerivedType(typeof(StaticAbility), nameof(StaticAbility))]
[JsonDerivedType(typeof(ReplacementAbility), nameof(ReplacementAbility))]
public abstract record Ability
{
    /// <summary>The printed line this ability came from, for the dispute view and the judge. The engine ignores it.</summary>
    public string? Text { get; init; }
}

/// <summary>
/// A keyword from CR 702 with its parameters: "Ward {2}" is Cost, "Bushido 2" is N,
/// "Protection from red" and "Affinity for artifacts" are Of, "Partner with Name" is Detail.
/// <para>
/// <paramref name="Cost"/> is a printed mana string and takes nothing else. A keyword whose cost has
/// a non-mana part — escape's "{1}{B}{B}, Exile four other cards from your graveyard" (CR 702.138a),
/// and the "sacrifice a creature" half of several others — puts the whole cost in
/// <paramref name="Costs"/> as Cost records instead, mana included as a ManaCost entry, and leaves
/// <paramref name="Cost"/> null. Set one or the other, never both: two spellings of the same cost is
/// how a card silently gets paid twice or not at all.
/// </para>
/// <para>
/// Set <paramref name="Repeatable"/> when the printed word says the cost may be paid more than once
/// as the spell is cast: multikicker (CR 702.32b) and replicate-shaped keywords. Plain kicker
/// (CR 702.32a) is paid once or not at all and leaves it false. A repeatable keyword is what makes
/// the TimesKicked amount countable; without the flag, casting refuses a second payment, so a
/// multikicker card translated without it is castable but never kicked twice.
/// </para>
/// <para>
/// <c>OfPlayer</c>: "enchant player", "enchant opponent" (CR 702.5b): what this Aura may enchant when it is a
/// player rather than an object. An enchant ability with neither <c>Of</c> nor <c>OfPlayer</c> admits
/// any object or player; one with <c>Of</c> only admits objects.
/// </para>
/// </summary>
[RuleRef("702")]
public sealed record KeywordAbility(
    KeywordKind Keyword,
    string? Cost = null,
    Amount? N = null,
    ObjectFilter? Of = null,
    string? Detail = null,
    Condition? Condition = null,
    IReadOnlyList<Cost>? Costs = null,
    bool Repeatable = false,
    PlayerFilter? OfPlayer = null) : Ability;

/// <summary>What an instant or sorcery does when it resolves, CR 113.3a.</summary>
[RuleRef("113.3a")]
public sealed record SpellAbility(Effect Do) : Ability;

/// <summary>"When", "whenever", "at", CR 603. <paramref name="Where"/> is the zone the ability functions in.</summary>
[RuleRef("603")]
public sealed record TriggeredAbility(Trigger When, Effect Do, Condition? If = null, bool Optional = false, Zone Where = Zone.Battlefield, bool OncePerTurn = false) : Ability;

/// <summary>
/// "[Cost]: [Effect]", CR 602. Loyalty abilities carry a <see cref="LoyaltyCost"/>; mana abilities
/// are recognised by the engine, CR 605.
/// <paramref name="PayOnlyWith"/> is the printed rider "activate only using mana produced by a
/// creature" read as a colour restriction: the mana spent on this ability's cost must be of those
/// colours (CR 106.6b, the spend restriction's other half). Leave it null for the ordinary case,
/// where any mana pays.
/// <paramref name="PayOnlyFrom"/> is the other half of that rider: "activate only using mana
/// produced by artifacts" names the source the mana came from rather than its colour, and
/// a colour cannot say it (CR 106.6b). The filter describes the permanent whose mana ability
/// produced the mana. Set one field or the other, never both — a card prints one restriction, and
/// two would be read as an "and" no card asked for. A restriction neither can express goes to a
/// primitive request rather than being dropped, because paying with the wrong mana is a different
/// game action, not a rounding error.
/// </summary>
[RuleRef("602")]
public sealed record ActivatedAbility(
    IReadOnlyList<Cost> Costs,
    Effect Do,
    Timing Timing = Timing.Any,
    Zone Where = Zone.Battlefield,
    Condition? OnlyIf = null,
    Amount? MaxPerTurn = null,
    bool AnyPlayerMayActivate = false,
    Color? PayOnlyWith = null,
    ObjectFilter? PayOnlyFrom = null) : Ability;

/// <summary>A continuous effect from a static ability, CR 604. Applies to objects via <paramref name="To"/> or to players via <paramref name="Players"/>.</summary>
[RuleRef("604")]
public sealed record StaticAbility(
    ContinuousEffect Effect,
    Selector? To = null,
    PlayerRef? Players = null,
    Condition? While = null,
    Zone Where = Zone.Battlefield,
    bool CharacteristicDefining = false) : Ability;

/// <summary>"If ... would ..., instead", "as ... enters", CR 614.</summary>
[RuleRef("614")]
public sealed record ReplacementAbility(ReplacedEvent Instead, Effect With, Condition? If = null, bool Optional = false, Zone Where = Zone.Battlefield, bool SelfOnly = false) : Ability;

/// <summary>
/// A token to create, CR 111. The pipeline fills it from Scryfall's <c>all_parts</c> and the
/// token's own card object; <paramref name="ScryfallOracleId"/> keeps the link.
/// </summary>
[RuleRef("111")]
public sealed record TokenSpec(
    string Name,
    Color Colors,
    CardType Types,
    IReadOnlyList<string>? Subtypes = null,
    Supertype Supertypes = Supertype.None,
    int? Power = null,
    int? Toughness = null,
    IReadOnlyList<Ability>? Abilities = null,
    string? ScryfallOracleId = null,
    bool IsPredefined = false);

/// <summary>A Class card's level, CR 716: the cost to reach it and what it adds.</summary>
[RuleRef("716")]
public sealed record ClassLevel(int Level, string? Cost, IReadOnlyList<Ability> Abilities);

/// <summary>A leveler's band, CR 711: "LEVEL 1-3 2/2 flying".</summary>
[RuleRef("711")]
public sealed record LevelBand(int From, int? To, PrintedStat? Power, PrintedStat? Toughness, IReadOnlyList<Ability> Abilities);
