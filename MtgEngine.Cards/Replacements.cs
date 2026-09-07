using System.Text.Json.Serialization;

namespace MtgEngine.Cards;

/// <summary>
/// The event a replacement effect watches for, CR 614. The ability's <c>With</c> effect runs
/// instead of the event, with <see cref="Triggering"/> bound to the object the event was about.
/// </summary>
[RuleRef("614")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(WouldEnterBattlefield), nameof(WouldEnterBattlefield))]
[JsonDerivedType(typeof(WouldLeaveBattlefield), nameof(WouldLeaveBattlefield))]
[JsonDerivedType(typeof(WouldDie), nameof(WouldDie))]
[JsonDerivedType(typeof(WouldBePutIntoZone), nameof(WouldBePutIntoZone))]
[JsonDerivedType(typeof(WouldBeDestroyed), nameof(WouldBeDestroyed))]
[JsonDerivedType(typeof(WouldBeDealtDamage), nameof(WouldBeDealtDamage))]
[JsonDerivedType(typeof(PlayerWouldBeDealtDamage), nameof(PlayerWouldBeDealtDamage))]
[JsonDerivedType(typeof(WouldDealDamage), nameof(WouldDealDamage))]
[JsonDerivedType(typeof(WouldDraw), nameof(WouldDraw))]
[JsonDerivedType(typeof(WouldGainLife), nameof(WouldGainLife))]
[JsonDerivedType(typeof(WouldLoseLife), nameof(WouldLoseLife))]
[JsonDerivedType(typeof(WouldDiscard), nameof(WouldDiscard))]
[JsonDerivedType(typeof(WouldMill), nameof(WouldMill))]
[JsonDerivedType(typeof(WouldCreateTokens), nameof(WouldCreateTokens))]
[JsonDerivedType(typeof(WouldPutCounters), nameof(WouldPutCounters))]
[JsonDerivedType(typeof(WouldBeCountered), nameof(WouldBeCountered))]
[JsonDerivedType(typeof(WouldBeExiled), nameof(WouldBeExiled))]
[JsonDerivedType(typeof(WouldBeSacrificed), nameof(WouldBeSacrificed))]
[JsonDerivedType(typeof(WouldUntap), nameof(WouldUntap))]
[JsonDerivedType(typeof(WouldTap), nameof(WouldTap))]
[JsonDerivedType(typeof(WouldBeTapped), nameof(WouldBeTapped))]
[JsonDerivedType(typeof(WouldAddMana), nameof(WouldAddMana))]
[JsonDerivedType(typeof(WouldLoseGame), nameof(WouldLoseGame))]
[JsonDerivedType(typeof(WouldBeTurnedFaceUp), nameof(WouldBeTurnedFaceUp))]
[JsonDerivedType(typeof(WouldBeginStep), nameof(WouldBeginStep))]
[JsonDerivedType(typeof(WouldShuffle), nameof(WouldShuffle))]
public abstract record ReplacedEvent;

/// <summary>"As this creature enters", "if a creature would enter under your control".</summary>
[RuleRef("614.1c")]
public sealed record WouldEnterBattlefield(ObjectFilter What, PlayerRef? UnderControlOf = null) : ReplacedEvent;

public sealed record WouldLeaveBattlefield(ObjectFilter What) : ReplacedEvent;

/// <summary>"If this creature would die, exile it instead."</summary>
public sealed record WouldDie(ObjectFilter What) : ReplacedEvent;

/// <summary>"If a card would be put into your graveyard from anywhere".</summary>
public sealed record WouldBePutIntoZone(ObjectFilter What, Zone To, Zone? From = null) : ReplacedEvent;

public sealed record WouldBeDestroyed(ObjectFilter What) : ReplacedEvent;

[RuleRef("615")]
public sealed record WouldBeDealtDamage(ObjectFilter To, ObjectFilter? By = null, bool? Combat = null) : ReplacedEvent;

[RuleRef("615")]
public sealed record PlayerWouldBeDealtDamage(PlayerRef Who, ObjectFilter? By = null, bool? Combat = null) : ReplacedEvent;

public sealed record WouldDealDamage(ObjectFilter Source, bool? Combat = null) : ReplacedEvent;

[RuleRef("121.4")]
public sealed record WouldDraw(PlayerRef Who, bool? FirstEachTurn = null) : ReplacedEvent;

public sealed record WouldGainLife(PlayerRef Who) : ReplacedEvent;

public sealed record WouldLoseLife(PlayerRef Who) : ReplacedEvent;

public sealed record WouldDiscard(PlayerRef Who, ObjectFilter? What = null) : ReplacedEvent;

public sealed record WouldMill(PlayerRef Who) : ReplacedEvent;

public sealed record WouldCreateTokens(PlayerRef Who, ObjectFilter? What = null) : ReplacedEvent;

public sealed record WouldPutCounters(ObjectFilter On, string? Counter = null) : ReplacedEvent;

public sealed record WouldBeCountered(ObjectFilter Spell) : ReplacedEvent;

public sealed record WouldBeExiled(ObjectFilter What) : ReplacedEvent;

public sealed record WouldBeSacrificed(ObjectFilter What) : ReplacedEvent;

public sealed record WouldUntap(ObjectFilter What, bool DuringUntapStepOnly = true) : ReplacedEvent;

public sealed record WouldTap(ObjectFilter What, bool ForMana = false) : ReplacedEvent;

public sealed record WouldBeTapped(ObjectFilter What) : ReplacedEvent;

[RuleRef("106.7")]
public sealed record WouldAddMana(PlayerRef Who, ObjectFilter? FromSource = null) : ReplacedEvent;

[RuleRef("104.3")]
public sealed record WouldLoseGame(PlayerRef Who) : ReplacedEvent;

public sealed record WouldBeTurnedFaceUp(ObjectFilter What) : ReplacedEvent;

/// <summary>"Skip your draw step", "if a player would begin their turn".</summary>
[RuleRef("614.10")]
public sealed record WouldBeginStep(Step Step, PlayerRef Whose) : ReplacedEvent;

public sealed record WouldShuffle(PlayerRef Who) : ReplacedEvent;
