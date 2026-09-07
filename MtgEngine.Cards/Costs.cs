using System.Text.Json.Serialization;

namespace MtgEngine.Cards;

/// <summary>
/// What a player pays, CR 118. Mana is written as printed ("{1}{G}", "{X}{X}"); everything
/// else is a record. A cost that asks for a choice takes it with the activation, so the
/// engine never suspends a payment.
/// </summary>
[RuleRef("118")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ManaCost), nameof(ManaCost))]
[JsonDerivedType(typeof(TapSelf), nameof(TapSelf))]
[JsonDerivedType(typeof(UntapSelf), nameof(UntapSelf))]
[JsonDerivedType(typeof(TapOther), nameof(TapOther))]
[JsonDerivedType(typeof(SacrificeSelf), nameof(SacrificeSelf))]
[JsonDerivedType(typeof(SacrificeCost), nameof(SacrificeCost))]
[JsonDerivedType(typeof(DiscardCost), nameof(DiscardCost))]
[JsonDerivedType(typeof(DiscardSelfCost), nameof(DiscardSelfCost))]
[JsonDerivedType(typeof(DiscardHand), nameof(DiscardHand))]
[JsonDerivedType(typeof(PayLife), nameof(PayLife))]
[JsonDerivedType(typeof(PayEnergy), nameof(PayEnergy))]
[JsonDerivedType(typeof(RemoveCountersCost), nameof(RemoveCountersCost))]
[JsonDerivedType(typeof(PutCountersCost), nameof(PutCountersCost))]
[JsonDerivedType(typeof(LoyaltyCost), nameof(LoyaltyCost))]
[JsonDerivedType(typeof(ExileFromZoneCost), nameof(ExileFromZoneCost))]
[JsonDerivedType(typeof(ExileSelfCost), nameof(ExileSelfCost))]
[JsonDerivedType(typeof(ReturnToHandCost), nameof(ReturnToHandCost))]
[JsonDerivedType(typeof(RevealCost), nameof(RevealCost))]
[JsonDerivedType(typeof(MillCost), nameof(MillCost))]
[JsonDerivedType(typeof(CollectEvidenceCost), nameof(CollectEvidenceCost))]
[JsonDerivedType(typeof(ForageCost), nameof(ForageCost))]
[JsonDerivedType(typeof(TapUntappedCost), nameof(TapUntappedCost))]
[JsonDerivedType(typeof(ChooseCost), nameof(ChooseCost))]
[JsonDerivedType(typeof(Costs), nameof(Costs))]
public abstract record Cost;

/// <summary>Mana as printed: "{2}{W}", "{X}{R}", "{G/U}", "{2/B}", "{C}", "{S}".</summary>
[RuleRef("107.4")]
public sealed record ManaCost(string Mana) : Cost;

[RuleRef("118.1")]
public sealed record TapSelf : Cost;

public sealed record UntapSelf : Cost;

/// <summary>"Tap an untapped creature you control": tap other permanents matching a filter.</summary>
public sealed record TapOther(ObjectFilter What, Amount? Count = null) : Cost;

public sealed record SacrificeSelf : Cost;

public sealed record SacrificeCost(ObjectFilter What, Amount? Count = null) : Cost;

public sealed record DiscardCost(Amount? Count = null, ObjectFilter? What = null, bool Random = false) : Cost;

/// <summary>
/// "Discard this card": the card the ability is on is the one discarded, and no other card can pay
/// it. This is the cost cycling asks for — "[Cost], Discard this card: Draw a card" (CR 702.29a) —
/// and the same shape jump-start and other discard-this-card abilities use. Use DiscardCost only
/// when the text says "discard a card", where the player chooses which; a player must not be able
/// to pay a discard-this-card cost with a different card.
/// </summary>
[RuleRef("702.29a")]
public sealed record DiscardSelfCost : Cost;

public sealed record DiscardHand : Cost;

[RuleRef("119.4")]
public sealed record PayLife(Amount Amount) : Cost;

public sealed record PayEnergy(Amount Amount) : Cost;

public sealed record RemoveCountersCost(string Counter, Amount? Count = null, Selector? From = null) : Cost;

public sealed record PutCountersCost(string Counter, Amount? Count = null, Selector? On = null) : Cost;

/// <summary>A loyalty ability's cost: +N, −N or 0, CR 606.</summary>
[RuleRef("606.5")]
public sealed record LoyaltyCost(int Delta) : Cost;

/// <summary>"Exile a creature card from your graveyard", "exile N cards from your hand".</summary>
public sealed record ExileFromZoneCost(Zone From, ObjectFilter? What = null, Amount? Count = null) : Cost;

public sealed record ExileSelfCost : Cost;

public sealed record ReturnToHandCost(ObjectFilter What, Amount? Count = null) : Cost;

public sealed record RevealCost(ObjectFilter What, Amount? Count = null) : Cost;

public sealed record MillCost(Amount Count) : Cost;

[RuleRef("701.59")]
public sealed record CollectEvidenceCost(int N) : Cost;

[RuleRef("701.61")]
public sealed record ForageCost : Cost;

/// <summary>Convoke, improvise and "waterbend": tap untapped permanents in place of generic mana.</summary>
[RuleRef("702.51")]
public sealed record TapUntappedCost(ObjectFilter What, Amount? Count = null) : Cost;

/// <summary>"Pay {2} or sacrifice a creature": one of several costs, chosen when paying.</summary>
public sealed record ChooseCost(IReadOnlyList<Cost> Options) : Cost;

/// <summary>Several costs paid together.</summary>
public sealed record Costs(IReadOnlyList<Cost> All) : Cost;
