using System.Text.Json.Serialization;

namespace MtgEngine.Cards;

/// <summary>
/// A yes-or-no question about the game: an intervening "if" on a trigger, the "as long as" of a
/// static ability, the "if" of an effect. Ability words are conditions here, not nodes of their own.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Controls), nameof(Controls))]
[JsonDerivedType(typeof(Compare), nameof(Compare))]
[JsonDerivedType(typeof(IsYourTurn), nameof(IsYourTurn))]
[JsonDerivedType(typeof(IsStep), nameof(IsStep))]
[JsonDerivedType(typeof(Threshold), nameof(Threshold))]
[JsonDerivedType(typeof(Delirium), nameof(Delirium))]
[JsonDerivedType(typeof(Metalcraft), nameof(Metalcraft))]
[JsonDerivedType(typeof(Hellbent), nameof(Hellbent))]
[JsonDerivedType(typeof(Morbid), nameof(Morbid))]
[JsonDerivedType(typeof(Revolt), nameof(Revolt))]
[JsonDerivedType(typeof(Ferocious), nameof(Ferocious))]
[JsonDerivedType(typeof(Formidable), nameof(Formidable))]
[JsonDerivedType(typeof(Descend), nameof(Descend))]
[JsonDerivedType(typeof(Raid), nameof(Raid))]
[JsonDerivedType(typeof(SpellMastery), nameof(SpellMastery))]
[JsonDerivedType(typeof(Coven), nameof(Coven))]
[JsonDerivedType(typeof(Corrupted), nameof(Corrupted))]
[JsonDerivedType(typeof(FatefulHour), nameof(FatefulHour))]
[JsonDerivedType(typeof(Lieutenant), nameof(Lieutenant))]
[JsonDerivedType(typeof(Adamant), nameof(Adamant))]
[JsonDerivedType(typeof(Paradox), nameof(Paradox))]
[JsonDerivedType(typeof(CityBlessing), nameof(CityBlessing))]
[JsonDerivedType(typeof(HasKeyword), nameof(HasKeyword))]
[JsonDerivedType(typeof(IsAttacking), nameof(IsAttacking))]
[JsonDerivedType(typeof(IsBlocking), nameof(IsBlocking))]
[JsonDerivedType(typeof(IsTapped), nameof(IsTapped))]
[JsonDerivedType(typeof(IsOnBattlefield), nameof(IsOnBattlefield))]
[JsonDerivedType(typeof(IsInZone), nameof(IsInZone))]
[JsonDerivedType(typeof(Matches), nameof(Matches))]
[JsonDerivedType(typeof(WasKickedCondition), nameof(WasKickedCondition))]
[JsonDerivedType(typeof(CastFromZone), nameof(CastFromZone))]
[JsonDerivedType(typeof(PaidAlternativeCost), nameof(PaidAlternativeCost))]
[JsonDerivedType(typeof(YouAreMonarch), nameof(YouAreMonarch))]
[JsonDerivedType(typeof(YouHaveInitiative), nameof(YouHaveInitiative))]
[JsonDerivedType(typeof(ChoiceIs), nameof(ChoiceIs))]
[JsonDerivedType(typeof(StoredIs), nameof(StoredIs))]
[JsonDerivedType(typeof(TargetsAre), nameof(TargetsAre))]
[JsonDerivedType(typeof(FirstTimeThisTurn), nameof(FirstTimeThisTurn))]
[JsonDerivedType(typeof(NotCondition), nameof(NotCondition))]
[JsonDerivedType(typeof(And), nameof(And))]
[JsonDerivedType(typeof(Or), nameof(Or))]
public abstract record Condition;

/// <summary>"if you control three or more artifacts": a count of objects compared to a number.</summary>
public sealed record Controls(PlayerRef Who, ObjectFilter What, Comparison Count) : Condition;

/// <summary>Any two amounts compared: "if your life total is less than an opponent's".</summary>
public sealed record Compare(Amount Left, ComparisonOp Op, Amount Right) : Condition;

public sealed record IsYourTurn(bool Value = true) : Condition;

public sealed record IsStep(Step Step) : Condition;

[RuleRef("702")]
public sealed record Threshold(PlayerRef Who) : Condition;

public sealed record Delirium(PlayerRef Who) : Condition;

public sealed record Metalcraft(PlayerRef Who) : Condition;

public sealed record Hellbent(PlayerRef Who) : Condition;

/// <summary>A creature died this turn.</summary>
public sealed record Morbid : Condition;

/// <summary>A permanent you controlled left the battlefield this turn.</summary>
public sealed record Revolt(PlayerRef Who) : Condition;

/// <summary>You control a creature with power 4 or greater.</summary>
public sealed record Ferocious(PlayerRef Who) : Condition;

/// <summary>Creatures you control have total power 8 or greater.</summary>
public sealed record Formidable(PlayerRef Who) : Condition;

/// <summary>Descend N: N or more permanent cards in your graveyard.</summary>
public sealed record Descend(PlayerRef Who, int N) : Condition;

/// <summary>You attacked with a creature this turn.</summary>
public sealed record Raid(PlayerRef Who) : Condition;

/// <summary>Two or more instant and/or sorcery cards in your graveyard.</summary>
public sealed record SpellMastery(PlayerRef Who) : Condition;

/// <summary>Three or more creatures with different powers.</summary>
public sealed record Coven(PlayerRef Who) : Condition;

/// <summary>An opponent has three or more poison counters.</summary>
public sealed record Corrupted : Condition;

/// <summary>Life total 5 or less.</summary>
public sealed record FatefulHour(PlayerRef Who) : Condition;

/// <summary>You control your commander.</summary>
public sealed record Lieutenant(PlayerRef Who) : Condition;

/// <summary>Three or more mana of one color was spent to cast this spell.</summary>
public sealed record Adamant(Color Color) : Condition;

/// <summary>You cast a spell from anywhere other than your hand this turn.</summary>
public sealed record Paradox(PlayerRef Who) : Condition;

[RuleRef("702.131")]
public sealed record CityBlessing(PlayerRef Who) : Condition;

public sealed record HasKeyword(Selector Of, KeywordKind Keyword) : Condition;

public sealed record IsAttacking(Selector Of) : Condition;

public sealed record IsBlocking(Selector Of) : Condition;

public sealed record IsTapped(Selector Of) : Condition;

public sealed record IsOnBattlefield(Selector Of) : Condition;

public sealed record IsInZone(Selector Of, Zone Zone) : Condition;

/// <summary>The selected object matches a filter: "if it's a creature card".</summary>
public sealed record Matches(Selector Of, ObjectFilter Filter) : Condition;

[RuleRef("702.33")]
public sealed record WasKickedCondition(int? Times = null) : Condition;

public sealed record CastFromZone(Zone From) : Condition;

public sealed record PaidAlternativeCost(KeywordKind? Which = null) : Condition;

[RuleRef("724")]
public sealed record YouAreMonarch : Condition;

[RuleRef("725")]
public sealed record YouHaveInitiative : Condition;

/// <summary>An earlier choice had a given value: "if the chosen color is red".</summary>
public sealed record ChoiceIs(string Key, string Value) : Condition;

/// <summary>A stored count compared: "if two or more cards were discarded this way".</summary>
public sealed record StoredIs(string Key, Comparison Compare) : Condition;

/// <summary>The chosen targets satisfy a filter, checked at resolution.</summary>
public sealed record TargetsAre(ObjectFilter Filter) : Condition;

/// <summary>"the first time this ability resolves each turn".</summary>
public sealed record FirstTimeThisTurn : Condition;

public sealed record NotCondition(Condition Of) : Condition;

public sealed record And(IReadOnlyList<Condition> Of) : Condition;

public sealed record Or(IReadOnlyList<Condition> Of) : Condition;
