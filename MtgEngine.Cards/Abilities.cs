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
/// </summary>
[RuleRef("702")]
public sealed record KeywordAbility(KeywordKind Keyword, string? Cost = null, Amount? N = null, ObjectFilter? Of = null, string? Detail = null, Condition? Condition = null) : Ability;

/// <summary>What an instant or sorcery does when it resolves, CR 113.3a.</summary>
[RuleRef("113.3a")]
public sealed record SpellAbility(Effect Do) : Ability;

/// <summary>"When", "whenever", "at", CR 603. <paramref name="Where"/> is the zone the ability functions in.</summary>
[RuleRef("603")]
public sealed record TriggeredAbility(Trigger When, Effect Do, Condition? If = null, bool Optional = false, Zone Where = Zone.Battlefield, bool OncePerTurn = false) : Ability;

/// <summary>"[Cost]: [Effect]", CR 602. Loyalty abilities carry a <see cref="LoyaltyCost"/>; mana abilities are recognised by the engine, CR 605.</summary>
[RuleRef("602")]
public sealed record ActivatedAbility(
    IReadOnlyList<Cost> Costs,
    Effect Do,
    Timing Timing = Timing.Any,
    Zone Where = Zone.Battlefield,
    Condition? OnlyIf = null,
    Amount? MaxPerTurn = null,
    bool AnyPlayerMayActivate = false) : Ability;

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
