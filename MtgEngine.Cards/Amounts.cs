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
[JsonDerivedType(typeof(TriggeringDamage), nameof(TriggeringDamage))]
[JsonDerivedType(typeof(AttackersAgainst), nameof(AttackersAgainst))]
[JsonDerivedType(typeof(CardsDrawnThisTurn), nameof(CardsDrawnThisTurn))]
[JsonDerivedType(typeof(LandsEnteredThisTurn), nameof(LandsEnteredThisTurn))]
[JsonDerivedType(typeof(HighestChosen), nameof(HighestChosen))]
[JsonDerivedType(typeof(LowestChosen), nameof(LowestChosen))]
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

/// <summary>
/// "the greatest power among creatures you control". <paramref name="Of"/> is read once per object
/// <paramref name="Among"/> admits, with that object bound as Self while it is read: "the greatest
/// power among creatures you control" is Of: PowerOf(Self). It is not Triggering, which stays the
/// trigger's own object throughout, and it is not the card the ability is printed on, which is
/// out of reach inside the amount. The same binding rules TotalAmong.
/// </summary>
public sealed record GreatestAmong(ObjectFilter Among, Amount Of) : Amount;

/// <summary>
/// "the total power of creatures you control". <paramref name="Of"/> is read once per object
/// <paramref name="Among"/> admits, with that object bound as Self while it is read, and the
/// readings are summed: Of: PowerOf(Self). Self here is the candidate, never the source of the
/// ability, and never Triggering — the same binding as GreatestAmong.
/// </summary>
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

/// <summary>
/// "that much damage", "draw that many cards": how much damage the event that triggered this
/// ability dealt, CR 603.10 — the trigger's own information, read as the ability resolves and not
/// recomputed from the board. It is the amount half of every damage trigger, whether the ability is
/// printed or was created as a <see cref="DelayedTrigger"/> this turn.
/// <para>
/// It is not <see cref="DamageDealtThisTurn"/>, which totals a turn's damage and would count an
/// earlier hit as well, and it is not a <see cref="Stored"/> key: no step of this card dealt the
/// damage, so nothing on the card could have stored it. The ability it appears in must trigger on
/// damage — DealsDamage, IsDealtDamage or PlayerIsDealtDamage, its own or its delayed trigger's —
/// or there is no event to read and the amount is a lost sentence (L028).
/// </para>
/// </summary>
[RuleRef("603.10")]
public sealed record TriggeringDamage : Amount;

/// <summary>
/// "two or more of those creatures are attacking you and/or planeswalkers you control": the
/// creatures of the current declaration of attackers, CR 508.1, that are attacking the player
/// <paramref name="Who"/> names — and, while <paramref name="IncludingPermanentsTheyControl"/>
/// stands, the planeswalkers and battles that player controls, because a creature attacking one of
/// those is attacking that player's side of the board and the printed phrase says "and/or".
/// <para>
/// It counts the declaration, so it is read in the same moment as
/// <see cref="AttacksWithCreatures"/> — the condition of an ability that trigger put on the stack,
/// CR 603.4 — and it is not <c>CountOf</c> with an Attacking filter, which counts every attacking
/// creature in the combat whoever declared it and whoever it is aimed at, so a two-player attack in
/// a three-player game reads the wrong number. Clear the flag only for a card that names the player
/// alone.
/// </para>
/// </summary>
[RuleRef("508.1")]
public sealed record AttackersAgainst(PlayerRef Who, bool IncludingPermanentsTheyControl = true) : Amount;

/// <summary>
/// "the number of cards you have drawn this turn", CR 121.1: a draw is one event per card, so this
/// counts events and not the cards still in hand — a card drawn and then discarded, played or exiled
/// is still a card that player drew this turn. It is per player and per turn.
/// <para>
/// It is not <see cref="CardsInHandOf"/>, which counts a hand however those cards got there, and it
/// is not the <see cref="DrawnThisTurn"/> predicate, which describes one card in a hand rather than
/// counting a turn of draws; a card drawn and then discarded is counted here and matched by neither.
/// </para>
/// </summary>
[RuleRef("121.1")]
public sealed record CardsDrawnThisTurn(PlayerRef Who) : Amount;

/// <summary>
/// The lands that entered the battlefield under a player control this turn, CR 121.1: the other
/// event a turn keeps per player. Every land that entered counts, whether it was played from a hand
/// (CR 305.1), put onto the battlefield by a search or returned by an effect, and a land that has
/// since left still counts — the count is of the entering and not of the board.
/// <para>
/// It is not <c>CountOf</c> over a land filter with an EnteredThisTurn predicate, which counts the
/// lands still on the battlefield and reads a smaller number the moment one is sacrificed, and it is
/// not the land drop the turn structure limits (CR 305.2), which counts plays and not entries.
/// </para>
/// </summary>
[RuleRef("121.1")]
public sealed record LandsEnteredThisTurn(PlayerRef Who) : Amount;

/// <summary>
/// The highest number chosen in a secret simultaneous choice, CR 700.2 with 101.4: the greatest of
/// the numbers the players chose under <paramref name="Key"/>, read once they are revealed.
/// <paramref name="Key"/> names the <see cref="ChooseNumberSecretly"/> step of the same ability that
/// asked for them, which is the only thing that produces it (L035).
/// <para>
/// It is not <see cref="ChosenNumber"/>, which is one player choice and cannot say whose is
/// greatest, and it is not <see cref="GreatestAmong"/>, which reads a characteristic off objects on
/// the board. The players who chose it are <see cref="PlayerWhoChose"/>, and a tie names them all.
/// </para>
/// </summary>
[RuleRef("700.2")]
public sealed record HighestChosen(string Key) : Amount;

/// <summary>The lowest number chosen in that same secret choice, CR 700.2: the mirror of <see cref="HighestChosen"/>.</summary>
[RuleRef("700.2")]
public sealed record LowestChosen(string Key) : Amount;

public sealed record TurnNumber : Amount;

public sealed record Sum(Amount Left, Amount Right) : Amount;

public sealed record Difference(Amount Left, Amount Right) : Amount;

public sealed record Times(Amount Value, int Factor) : Amount;

public sealed record Half(Amount Value, bool RoundUp) : Amount;

public sealed record Max(Amount Left, Amount Right) : Amount;

public sealed record Min(Amount Left, Amount Right) : Amount;
