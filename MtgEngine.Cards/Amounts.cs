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
[JsonDerivedType(typeof(ManaSpentOnTriggering), nameof(ManaSpentOnTriggering))]
[JsonDerivedType(typeof(TriggeringPreventedDamage), nameof(TriggeringPreventedDamage))]
[JsonDerivedType(typeof(TimesKicked), nameof(TimesKicked))]
[JsonDerivedType(typeof(SpellsCastThisTurn), nameof(SpellsCastThisTurn))]
[JsonDerivedType(typeof(DamageDealtThisTurn), nameof(DamageDealtThisTurn))]
[JsonDerivedType(typeof(TriggeringDamage), nameof(TriggeringDamage))]
[JsonDerivedType(typeof(TriggeringLifeGained), nameof(TriggeringLifeGained))]
[JsonDerivedType(typeof(TriggeringLifeLost), nameof(TriggeringLifeLost))]
[JsonDerivedType(typeof(AttackersAgainst), nameof(AttackersAgainst))]
[JsonDerivedType(typeof(CardsDrawnThisTurn), nameof(CardsDrawnThisTurn))]
[JsonDerivedType(typeof(LandsEnteredThisTurn), nameof(LandsEnteredThisTurn))]
[JsonDerivedType(typeof(HighestChosen), nameof(HighestChosen))]
[JsonDerivedType(typeof(LowestChosen), nameof(LowestChosen))]
[JsonDerivedType(typeof(LifeLostThisTurn), nameof(LifeLostThisTurn))]
[JsonDerivedType(typeof(DistinctNameCount), nameof(DistinctNameCount))]
[JsonDerivedType(typeof(CommanderCastsThisGame), nameof(CommanderCastsThisGame))]
[JsonDerivedType(typeof(TurnNumber), nameof(TurnNumber))]
[JsonDerivedType(typeof(Sum), nameof(Sum))]
[JsonDerivedType(typeof(Difference), nameof(Difference))]
[JsonDerivedType(typeof(Times), nameof(Times))]
[JsonDerivedType(typeof(Half), nameof(Half))]
[JsonDerivedType(typeof(Max), nameof(Max))]
[JsonDerivedType(typeof(Min), nameof(Min))]
[JsonDerivedType(typeof(GreatestDamageDealtThisTurn), nameof(GreatestDamageDealtThisTurn))]
[JsonDerivedType(typeof(CreaturesDiedThisTurn), nameof(CreaturesDiedThisTurn))]
[JsonDerivedType(typeof(PreventedDamage), nameof(PreventedDamage))]
[JsonDerivedType(typeof(GreatestSameNameCount), nameof(GreatestSameNameCount))]
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
/// "If no mana was spent to cast it": the mana paid for the spell that triggered this ability,
/// CR 400.7d — what was paid to cast a spell is information a later ability may read, and CR 601.2h
/// is where that payment happened. It is the triggering spell's payment and never this object's own,
/// which is <see cref="ManaSpentOnThis"/>: "whenever a player casts a spell, if no mana was spent to
/// cast it" asks about somebody else's spell, and ManaSpentOnThis there answers about this one.
/// <para>
/// "No mana was spent" is this amount inside a Compare against zero and not a condition of its own:
/// the number is what the card reads, and every other question about it ("one or more", "for each
/// colour") is the same number compared differently. The ability must trigger on a spell being cast
/// — SpellCast, its own or its delayed trigger's — or there is no spell whose payment this could be
/// (L043).
/// </para>
/// </summary>
[RuleRef("400.7d")]
public sealed record ManaSpentOnTriggering(Color? OfColor = null) : Amount;

/// <summary>
/// "When damage is prevented this way, this creature deals that much damage to any other target":
/// the damage a prevention effect actually prevented, CR 615.13 with CR 603.10 — the amount half of
/// a <see cref="DamagePrevented"/> trigger, fixed as the ability triggered and not recomputed when
/// it resolves.
/// <para>
/// It is not <see cref="TriggeringDamage"/>. Damage dealt and damage prevented are two different
/// numbers on one event: a shield that prevents 2 of 5 leaves 3 dealt, and an effect that read the
/// dealt amount for "that much" would deal the wrong number — or, for damage prevented in full,
/// deal nothing at all. The ability must trigger on damage being prevented, its own or its delayed
/// trigger's (L043).
/// </para>
/// </summary>
[RuleRef("615.13")]
public sealed record TriggeringPreventedDamage : Amount;

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
/// "if an opponent was dealt 7 or more damage this turn": the greatest damage dealt this turn to any
/// one player <paramref name="To"/> admits, CR 102.2 with CR 120.3. The printed sentence is
/// existential — <b>some</b> opponent — and the max over the group answers it exactly, because the
/// greatest is at least seven precisely when one of them was dealt at least seven.
/// <para>
/// It is not <see cref="DamageDealtThisTurn"/> with <see cref="AnOpponent"/> in its
/// <c>To</c>: that names some opponent rather than one to read a number off, has no number of its
/// own and is refused (L041). It is not a total either — a <see cref="Sum"/> over opponents is true
/// of a table that spread seven damage over three players, which the card does not say, and in a
/// duel the two agree, so the wrong reading survives a two-player test. Damage is tracked per
/// player, so this folds with a max and never a sum; a filter that claims both
/// <c>Opponent</c> and <c>IsYou</c> admits nobody (L045).
/// </para>
/// </summary>
[RuleRef("102.2")]
public sealed record GreatestDamageDealtThisTurn(PlayerFilter To, Selector? By = null) : Amount;

/// <summary>
/// "the number of creatures that died this turn", CR 700.4 — died means "is put into a graveyard
/// from the battlefield", so this counts the objects that made that move this turn and not the
/// cards sitting in a graveyard now. A creature that died and was returned, exiled or shuffled away
/// since is still counted; a creature card milled or discarded into a graveyard never died and is
/// not.
/// <para>
/// <paramref name="Matching"/> narrows within those deaths — "creatures you controlled", "Goblins" —
/// and null counts every creature that died this turn under any control. It is not
/// <see cref="CardsInZoneOf"/> over a graveyard, which reads the board now and gets both halves
/// wrong in both directions, and it is not <see cref="CountOf"/> with an <c>In: Graveyard</c>
/// filter for the same reason. A <paramref name="Matching"/> that names a zone is asking where a
/// creature that already left the battlefield is, which is not what "died" counts (L045).
/// </para>
/// </summary>
[RuleRef("700.4")]
public sealed record CreaturesDiedThisTurn(ObjectFilter? Matching = null) : Amount;

/// <summary>
/// "for each 1 damage prevented this way", CR 615: the damage the prevention effect this ability is
/// running actually prevented. A shield that prevents 2 of 5 leaves 3 dealt, and a shield that meets
/// no damage prevents nothing at all, so this is a number read out of the prevention as it applies
/// and never the damage the event would have dealt.
/// <para>
/// It is not <see cref="TriggeringPreventedDamage"/>, which is the amount half of a
/// <c>DamagePrevented</c> trigger — that one reads an event some other object's prevention caused
/// and fires afterwards; this one is inside the effect doing the preventing, in the same breath as
/// the word "instead". It is not <see cref="TriggeringDamage"/>, which is the damage that was dealt,
/// and it is not a <see cref="Stored"/> key: no step of this card put the number anywhere. The
/// ability it appears in has to prevent damage — a <c>PreventDamage</c> in the same ability — or
/// there is nothing prevented "this way" and the amount is a lost sentence (L045).
/// </para>
/// </summary>
[RuleRef("615")]
public sealed record PreventedDamage : Amount;

/// <summary>
/// "if you control three or more lands with the same name": the size of the largest group of objects
/// <paramref name="Filter"/> admits that share a name, CR 201.2 with 201.2a (two objects have the
/// same name if they have at least one name in common, and an object with no name shares its name
/// with nothing — not even another nameless object).
/// <para>
/// The sentence is about a group and not about a count, so it is this amount compared against three
/// and never a <see cref="CountOf"/>: a board of three differently named lands satisfies a plain
/// count and does not satisfy the card. The name is not written down anywhere — the card says "the
/// same name", not a name — so a <c>Named</c> on the filter is a different sentence and is refused
/// (L045); <see cref="SharesNameWith"/> is the other shape, and it asks about one named object
/// rather than about the biggest group.
/// </para>
/// </summary>
[RuleRef("201.2")]
public sealed record GreatestSameNameCount(ObjectFilter Filter) : Amount;

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
/// "you gain that much life": the life the event that triggered this ability gained, CR 603.10 — the
/// amount half of a life-gain trigger, fixed at the moment the ability triggered and not recomputed
/// when it resolves, exactly as <see cref="TriggeringDamage"/> is for damage.
/// <para>
/// It is not <see cref="LifeOf"/>, which reads a player's total, and it is not a
/// <see cref="Stored"/> key: no step of this card gained the life, so nothing on the card could
/// have stored it. The ability it appears in must trigger on life being gained — GainsLife, its own
/// or its delayed trigger's — or there is no event to read and the amount is a lost sentence
/// (L039). A trigger that watches life being lost reads <see cref="TriggeringLifeLost"/>: the two
/// are separate events, and one written for the other reads nothing.
/// </para>
/// </summary>
[RuleRef("603.10")]
public sealed record TriggeringLifeGained : Amount;

/// <summary>
/// "that much life" on a life-loss trigger, CR 603.10: the life the triggering event lost, the
/// mirror of <see cref="TriggeringLifeGained"/> and read the same way. Life lost is not damage — a
/// payment, an edict and a Lava Spike all lose life and only one is damage — so this is not
/// <see cref="TriggeringDamage"/>, and the ability must trigger on LosesLife (L039).
/// </summary>
[RuleRef("603.10")]
public sealed record TriggeringLifeLost : Amount;

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

/// <summary>
/// The life lost this turn by the players <paramref name="Who"/> admits, CR 119.3, totalled across
/// them: "if an opponent lost life this turn" is a Compare of this against 0 over the opponents,
/// because the total is above zero exactly when at least one of them lost life.
/// <para>
/// Life lost is not damage (a payment, an edict and a burn spell all lose life and only one is
/// damage) and it is not life gained, so a life total that went down and came back up still counts
/// the loss. It reads the whole turn, not one event: <see cref="TriggeringLifeLost"/> is the amount
/// of the event a trigger fired on and answers nothing outside that trigger. A PlayerFilter and not
/// a PlayerRef, because the printed line names a group and is asked about all of them at once.
/// </para>
/// </summary>
[RuleRef("119.3")]
public sealed record LifeLostThisTurn(PlayerFilter Who) : Amount;

/// <summary>
/// "lands you control with different names", CR 201.2b: the number of distinct names among the
/// objects <paramref name="Filter"/> admits, so two Forests count once and two differently named
/// lands count twice.
/// <para>
/// It is not <c>CountOf</c> over the same filter, which counts the objects; the two agree only when
/// no two of them share a name, so a test on a board of distinct cards cannot tell them apart. An
/// object with no name is counted by neither (CR 201.2a).
/// </para>
/// </summary>
[RuleRef("201.2b")]
public sealed record DistinctNameCount(ObjectFilter Filter) : Amount;

/// <summary>
/// The number of times a commander has been cast from the command zone this game, CR 903.8 — the
/// count the commander tax is built on. <paramref name="Which"/> names the commander and defaults
/// to this object.
/// <para>
/// It counts casts from the command zone only: a commander cast from a hand it was put into does
/// not add to it, and neither does one that changed zones. It is a per-game count and is never
/// reset, so it is not a per-turn count and not a <c>Stored</c> key, which nothing on the card
/// wrote.
/// </para>
/// </summary>
[RuleRef("903.8")]
public sealed record CommanderCastsThisGame(Selector? Which = null) : Amount;
