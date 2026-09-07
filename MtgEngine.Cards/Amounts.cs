using System.Text.Json.Serialization;

namespace MtgEngine.Cards;

/// <summary>
/// A number a card refers to. Most are fixed; the rest are counted from the game when the effect
/// resolves. Every derived record is registered here, and a test asserts that the list is complete.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Fixed), nameof(Fixed))]
[JsonDerivedType(typeof(X), nameof(X))]
[JsonDerivedType(typeof(CountOf), nameof(CountOf))]
[JsonDerivedType(typeof(PlayerCount), nameof(PlayerCount))]
[JsonDerivedType(typeof(LifeOf), nameof(LifeOf))]
[JsonDerivedType(typeof(CardsInHandOf), nameof(CardsInHandOf))]
[JsonDerivedType(typeof(CardsInZoneOf), nameof(CardsInZoneOf))]
[JsonDerivedType(typeof(PowerOf), nameof(PowerOf))]
[JsonDerivedType(typeof(ToughnessOf), nameof(ToughnessOf))]
[JsonDerivedType(typeof(ManaValueOf), nameof(ManaValueOf))]
[JsonDerivedType(typeof(CountersOn), nameof(CountersOn))]
[JsonDerivedType(typeof(CountersOnPlayer), nameof(CountersOnPlayer))]
[JsonDerivedType(typeof(Devotion), nameof(Devotion))]
[JsonDerivedType(typeof(Domain), nameof(Domain))]
[JsonDerivedType(typeof(GreatestAmong), nameof(GreatestAmong))]
[JsonDerivedType(typeof(TotalAmong), nameof(TotalAmong))]
[JsonDerivedType(typeof(ChosenNumber), nameof(ChosenNumber))]
[JsonDerivedType(typeof(Stored), nameof(Stored))]
[JsonDerivedType(typeof(ManaSpentOnThis), nameof(ManaSpentOnThis))]
[JsonDerivedType(typeof(TimesKicked), nameof(TimesKicked))]
[JsonDerivedType(typeof(SpellsCastThisTurn), nameof(SpellsCastThisTurn))]
[JsonDerivedType(typeof(DamageDealtThisTurn), nameof(DamageDealtThisTurn))]
[JsonDerivedType(typeof(TurnNumber), nameof(TurnNumber))]
[JsonDerivedType(typeof(Sum), nameof(Sum))]
[JsonDerivedType(typeof(Difference), nameof(Difference))]
[JsonDerivedType(typeof(Times), nameof(Times))]
[JsonDerivedType(typeof(Half), nameof(Half))]
[JsonDerivedType(typeof(Max), nameof(Max))]
[JsonDerivedType(typeof(Min), nameof(Min))]
public abstract record Amount;

/// <summary>A literal number.</summary>
public sealed record Fixed(int N) : Amount;

/// <summary>The X paid or chosen for the spell or ability, CR 107.3.</summary>
[RuleRef("107.3")]
public sealed record X : Amount;

/// <summary>"the number of creatures you control": objects matching a filter.</summary>
public sealed record CountOf(ObjectFilter Filter) : Amount;

/// <summary>"the number of players" or opponents.</summary>
public sealed record PlayerCount(PlayerRef Who) : Amount;

public sealed record LifeOf(PlayerRef Who) : Amount;

public sealed record CardsInHandOf(PlayerRef Who) : Amount;

/// <summary>"the number of creature cards in your graveyard": cards in a zone, optionally filtered.</summary>
public sealed record CardsInZoneOf(PlayerRef Who, Zone Zone, ObjectFilter? Matching = null) : Amount;

public sealed record PowerOf(Selector Of) : Amount;

public sealed record ToughnessOf(Selector Of) : Amount;

public sealed record ManaValueOf(Selector Of) : Amount;

/// <summary>Counters of one kind on an object; any kind when <paramref name="Counter"/> is null.</summary>
public sealed record CountersOn(Selector On, string? Counter = null) : Amount;

/// <summary>Poison, energy, experience and other counters a player has.</summary>
public sealed record CountersOnPlayer(PlayerRef Who, string Counter) : Amount;

/// <summary>
/// Devotion to one or more colors, CR 700.5: the number of mana symbols of those colors among the
/// mana costs of permanents that player controls. Normally the colors are printed and go in
/// <paramref name="Colors"/> ("your devotion to black"). When the card instead reads "your devotion
/// to the chosen color", leave <paramref name="Colors"/> at Colorless and set
/// <paramref name="ChosenColorKey"/> to the key an earlier ChooseValue(ChooseWhat.Color, key) step
/// stored the colour under; the colour is then read from that choice when the amount is counted.
/// Never emit a fixed colour for a chosen one — the two are different cards.
/// </summary>
[RuleRef("700.5")]
public sealed record Devotion(PlayerRef Who, Color Colors = Color.Colorless, string? ChosenColorKey = null) : Amount;

/// <summary>The number of basic land types among lands a player controls.</summary>
public sealed record Domain(PlayerRef Who) : Amount;

/// <summary>"the greatest power among creatures you control".</summary>
public sealed record GreatestAmong(ObjectFilter Among, Amount Of) : Amount;

/// <summary>"the total power of creatures you control".</summary>
public sealed record TotalAmong(ObjectFilter Among, Amount Of) : Amount;

/// <summary>A number chosen earlier in the same effect with <see cref="Choose"/>.</summary>
public sealed record ChosenNumber(string Key) : Amount;

/// <summary>A number an earlier step produced: "the number of cards discarded this way".</summary>
public sealed record Stored(string Key) : Amount;

/// <summary>Mana spent to cast this spell, CR 601.2h.</summary>
[RuleRef("601.2h")]
public sealed record ManaSpentOnThis(Color? OfColor = null) : Amount;

/// <summary>
/// How many times this spell was kicked, CR 702.32b: multikicker's cost may be paid any number of
/// times as the spell is cast, and "for each time it was kicked" counts those payments. Use this
/// for multikicker and for replicate-shaped repeated optional costs; a card with plain kicker
/// (CR 702.32a, paid once or not at all) asks WasKickedCondition instead, which is a yes or no.
/// The KeywordAbility this counts must carry Repeatable, or nothing ever pays it twice.
/// </summary>
[RuleRef("702.32b")]
public sealed record TimesKicked : Amount;

public sealed record SpellsCastThisTurn(PlayerRef By, ObjectFilter? Matching = null) : Amount;

public sealed record DamageDealtThisTurn(Selector? By = null, PlayerRef? To = null) : Amount;

public sealed record TurnNumber : Amount;

public sealed record Sum(Amount Left, Amount Right) : Amount;

public sealed record Difference(Amount Left, Amount Right) : Amount;

public sealed record Times(Amount Value, int Factor) : Amount;

public sealed record Half(Amount Value, bool RoundUp) : Amount;

public sealed record Max(Amount Left, Amount Right) : Amount;

public sealed record Min(Amount Left, Amount Right) : Amount;
