using System.Text.Json.Serialization;

namespace MtgEngine.Cards;

/// <summary>
/// How long an effect lasts, CR 611. A static ability's effect lasts while its source is on
/// the battlefield and needs no duration; everything created by a resolving effect names one.
/// </summary>
[RuleRef("611")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UntilEndOfTurn), nameof(UntilEndOfTurn))]
[JsonDerivedType(typeof(UntilYourNextTurn), nameof(UntilYourNextTurn))]
[JsonDerivedType(typeof(UntilEndOfCombat), nameof(UntilEndOfCombat))]
[JsonDerivedType(typeof(UntilStep), nameof(UntilStep))]
[JsonDerivedType(typeof(Permanently), nameof(Permanently))]
[JsonDerivedType(typeof(WhileSourceOnBattlefield), nameof(WhileSourceOnBattlefield))]
[JsonDerivedType(typeof(WhileSourceAttached), nameof(WhileSourceAttached))]
[JsonDerivedType(typeof(WhileSourceInZone), nameof(WhileSourceInZone))]
[JsonDerivedType(typeof(UntilLeaves), nameof(UntilLeaves))]
[JsonDerivedType(typeof(ForAsLongAs), nameof(ForAsLongAs))]
[JsonDerivedType(typeof(UntilSourceLeavesOrCondition), nameof(UntilSourceLeavesOrCondition))]
public abstract record Duration;

[RuleRef("611.2a")]
public sealed record UntilEndOfTurn : Duration;

public sealed record UntilYourNextTurn : Duration;

public sealed record UntilEndOfCombat : Duration;

/// <summary>"until the beginning of your next upkeep", "until the end of your next turn".</summary>
public sealed record UntilStep(Step Step, PlayerRef Whose, bool Next = true) : Duration;

[RuleRef("611.2c")]
public sealed record Permanently : Duration;

[RuleRef("611.3")]
public sealed record WhileSourceOnBattlefield : Duration;

public sealed record WhileSourceAttached : Duration;

public sealed record WhileSourceInZone(Zone Zone) : Duration;

/// <summary>"for as long as that creature remains on the battlefield".</summary>
public sealed record UntilLeaves(Selector What) : Duration;

/// <summary>"for as long as you control an artifact".</summary>
public sealed record ForAsLongAs(Condition Condition) : Duration;

public sealed record UntilSourceLeavesOrCondition(Condition Condition) : Duration;

/// <summary>
/// What a continuous effect does to the objects or players it applies to, CR 611–613. Each
/// record belongs to one layer, which the interpreter reads from the record type.
/// </summary>
[RuleRef("613")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ModifyPt), nameof(ModifyPt))]
[JsonDerivedType(typeof(SetPt), nameof(SetPt))]
[JsonDerivedType(typeof(SwitchPt), nameof(SwitchPt))]
[JsonDerivedType(typeof(GrantAbility), nameof(GrantAbility))]
[JsonDerivedType(typeof(GrantKeyword), nameof(GrantKeyword))]
[JsonDerivedType(typeof(LoseAbility), nameof(LoseAbility))]
[JsonDerivedType(typeof(LoseAllAbilities), nameof(LoseAllAbilities))]
[JsonDerivedType(typeof(AddTypes), nameof(AddTypes))]
[JsonDerivedType(typeof(SetTypes), nameof(SetTypes))]
[JsonDerivedType(typeof(RemoveTypes), nameof(RemoveTypes))]
[JsonDerivedType(typeof(AddColors), nameof(AddColors))]
[JsonDerivedType(typeof(SetColors), nameof(SetColors))]
[JsonDerivedType(typeof(ControlBy), nameof(ControlBy))]
[JsonDerivedType(typeof(BecomesCopyOf), nameof(BecomesCopyOf))]
[JsonDerivedType(typeof(ChangeText), nameof(ChangeText))]
[JsonDerivedType(typeof(CantAttack), nameof(CantAttack))]
[JsonDerivedType(typeof(CantBlock), nameof(CantBlock))]
[JsonDerivedType(typeof(CantBeBlocked), nameof(CantBeBlocked))]
[JsonDerivedType(typeof(CantBeBlockedByMoreThan), nameof(CantBeBlockedByMoreThan))]
[JsonDerivedType(typeof(MustAttack), nameof(MustAttack))]
[JsonDerivedType(typeof(MustBlock), nameof(MustBlock))]
[JsonDerivedType(typeof(MustBeBlocked), nameof(MustBeBlocked))]
[JsonDerivedType(typeof(CantBeTargeted), nameof(CantBeTargeted))]
[JsonDerivedType(typeof(CantBeCountered), nameof(CantBeCountered))]
[JsonDerivedType(typeof(CantBeSacrificed), nameof(CantBeSacrificed))]
[JsonDerivedType(typeof(CantUntap), nameof(CantUntap))]
[JsonDerivedType(typeof(DoesntUntapNextUntapStep), nameof(DoesntUntapNextUntapStep))]
[JsonDerivedType(typeof(CantTransform), nameof(CantTransform))]
[JsonDerivedType(typeof(CantHaveCountersPlaced), nameof(CantHaveCountersPlaced))]
[JsonDerivedType(typeof(CantCastSpells), nameof(CantCastSpells))]
[JsonDerivedType(typeof(CantActivateAbilities), nameof(CantActivateAbilities))]
[JsonDerivedType(typeof(CantGainLife), nameof(CantGainLife))]
[JsonDerivedType(typeof(CantLoseGame), nameof(CantLoseGame))]
[JsonDerivedType(typeof(PlayerCant), nameof(PlayerCant))]
[JsonDerivedType(typeof(PlayerProtectionFromEverything), nameof(PlayerProtectionFromEverything))]
[JsonDerivedType(typeof(CantWinGame), nameof(CantWinGame))]
[JsonDerivedType(typeof(CantSearchLibraries), nameof(CantSearchLibraries))]
[JsonDerivedType(typeof(CantDrawMoreThan), nameof(CantDrawMoreThan))]
[JsonDerivedType(typeof(CostModifier), nameof(CostModifier))]
[JsonDerivedType(typeof(AbilityCostModifier), nameof(AbilityCostModifier))]
[JsonDerivedType(typeof(NoMaxHandSize), nameof(NoMaxHandSize))]
[JsonDerivedType(typeof(MaxHandSize), nameof(MaxHandSize))]
[JsonDerivedType(typeof(ExtraLandDrops), nameof(ExtraLandDrops))]
[JsonDerivedType(typeof(PreventDamage), nameof(PreventDamage))]
[JsonDerivedType(typeof(DamageMultiplier), nameof(DamageMultiplier))]
[JsonDerivedType(typeof(DealsDamageAsToughness), nameof(DealsDamageAsToughness))]
[JsonDerivedType(typeof(AssignDamageAsThoughUnblocked), nameof(AssignDamageAsThoughUnblocked))]
[JsonDerivedType(typeof(MayPlayFromZone), nameof(MayPlayFromZone))]
[JsonDerivedType(typeof(MayLookAtTopCard), nameof(MayLookAtTopCard))]
[JsonDerivedType(typeof(PlayWithHandRevealed), nameof(PlayWithHandRevealed))]
[JsonDerivedType(typeof(SkipStepEffect), nameof(SkipStepEffect))]
[JsonDerivedType(typeof(AdditionalCombatPhase), nameof(AdditionalCombatPhase))]
[JsonDerivedType(typeof(SpellsCostLifeInsteadOfMana), nameof(SpellsCostLifeInsteadOfMana))]
[JsonDerivedType(typeof(ManaProducesAdditional), nameof(ManaProducesAdditional))]
[JsonDerivedType(typeof(TokensEnterWithCounters), nameof(TokensEnterWithCounters))]
[JsonDerivedType(typeof(CountersDoubled), nameof(CountersDoubled))]
[JsonDerivedType(typeof(LifeGainDoubled), nameof(LifeGainDoubled))]
[JsonDerivedType(typeof(AbilitiesTriggerTwice), nameof(AbilitiesTriggerTwice))]
[JsonDerivedType(typeof(RegenerationShield), nameof(RegenerationShield))]
[JsonDerivedType(typeof(TreatAsHavingKeywordCounter), nameof(TreatAsHavingKeywordCounter))]
public abstract record ContinuousEffect;

[RuleRef("613.4c")]
public sealed record ModifyPt(Amount Power, Amount Toughness) : ContinuousEffect;

[RuleRef("613.4b")]
public sealed record SetPt(Amount Power, Amount Toughness) : ContinuousEffect;

[RuleRef("613.4e")]
public sealed record SwitchPt : ContinuousEffect;

[RuleRef("613.1f")]
public sealed record GrantAbility(Ability Ability) : ContinuousEffect;

[RuleRef("613.1f")]
public sealed record GrantKeyword(KeywordKind Keyword, string? Cost = null, Amount? N = null, ObjectFilter? Of = null) : ContinuousEffect;

[RuleRef("613.1f")]
public sealed record LoseAbility(KeywordKind Keyword) : ContinuousEffect;

[RuleRef("613.1f")]
public sealed record LoseAllAbilities : ContinuousEffect;

/// <summary>
/// Adds card types, subtypes or supertypes in layer 4 (CR 613.1d) while keeping the ones already
/// there: "is a creature in addition to its other types", "is also a Goblin". Set AllCreatureTypes
/// when the object is every creature type at once — changeling (CR 702.72a) and any text reading
/// "is every creature type" (CR 205.3d). Say it with that flag, never by listing creature types:
/// the list changes with every set, and the engine derives it rather than carrying card data.
/// </summary>
[RuleRef("613.1d")]
public sealed record AddTypes(CardType? Types = null, IReadOnlyList<string>? Subtypes = null, Supertype? Supertypes = null, bool AllCreatureTypes = false) : ContinuousEffect;

[RuleRef("613.1d")]
public sealed record SetTypes(CardType? Types = null, IReadOnlyList<string>? Subtypes = null, Supertype? Supertypes = null, bool KeepSubtypes = false) : ContinuousEffect;

[RuleRef("613.1d")]
public sealed record RemoveTypes(CardType? Types = null, IReadOnlyList<string>? Subtypes = null, Supertype? Supertypes = null, bool AllCreatureTypes = false) : ContinuousEffect;

[RuleRef("613.1e")]
public sealed record AddColors(Color Colors) : ContinuousEffect;

[RuleRef("613.1e")]
public sealed record SetColors(Color Colors) : ContinuousEffect;

[RuleRef("613.1b")]
public sealed record ControlBy(PlayerRef Who) : ContinuousEffect;

/// <summary>Layer 1: becomes a copy of another object, with the printed exceptions.</summary>
[RuleRef("707")]
public sealed record BecomesCopyOf(Selector Of, IReadOnlyList<ContinuousEffect>? Except = null, bool IsLegendary = false) : ContinuousEffect;

[RuleRef("613.1c")]
public sealed record ChangeText(string From, string To) : ContinuousEffect;

public sealed record CantAttack(PlayerRef? Player = null, ObjectFilter? Unless = null) : ContinuousEffect;

public sealed record CantBlock(ObjectFilter? Attackers = null) : ContinuousEffect;

/// <summary>"can't be blocked", "can't be blocked by creatures with power 2 or less", "can't be blocked except by two or more creatures".</summary>
public sealed record CantBeBlocked(ObjectFilter? By = null, bool ExceptByTwoOrMore = false) : ContinuousEffect;

public sealed record CantBeBlockedByMoreThan(int Count) : ContinuousEffect;

public sealed record MustAttack(PlayerRef? Player = null, bool IfAble = true) : ContinuousEffect;

public sealed record MustBlock(Selector? What = null, bool IfAble = true) : ContinuousEffect;

/// <summary>"All creatures able to block it do so."</summary>
public sealed record MustBeBlocked(ObjectFilter? By = null) : ContinuousEffect;

/// <summary>Shroud and hexproof are keywords; this is the general form: "can't be the target of red spells".</summary>
public sealed record CantBeTargeted(ObjectFilter? BySpells = null, PlayerRef? ByPlayers = null) : ContinuousEffect;

public sealed record CantBeCountered : ContinuousEffect;

public sealed record CantBeSacrificed : ContinuousEffect;

public sealed record CantUntap(bool DuringUntapStepOnly = true) : ContinuousEffect;

public sealed record DoesntUntapNextUntapStep : ContinuousEffect;

public sealed record CantTransform : ContinuousEffect;

public sealed record CantHaveCountersPlaced(string? Counter = null) : ContinuousEffect;

public sealed record CantCastSpells(PlayerRef Who, ObjectFilter? Matching = null) : ContinuousEffect;

public sealed record CantActivateAbilities(PlayerRef Who, ObjectFilter? OfSources = null, AbilityKind Ability = AbilityKind.Any) : ContinuousEffect;

public sealed record CantGainLife(PlayerRef Who) : ContinuousEffect;

public sealed record CantLoseGame(PlayerRef Who) : ContinuousEffect;

public sealed record CantWinGame(PlayerRef Who) : ContinuousEffect;

public sealed record CantSearchLibraries(PlayerRef Who) : ContinuousEffect;

/// <summary>
/// The things a player can be stopped from doing or having done to them, CR 614.1 read as a
/// prohibition. A [Flags] set so "can't lose the game and can't be targeted" is one effect.
/// </summary>
[Flags]
public enum PlayerRestriction
{
    None = 0,

    /// <summary>"Your life total can't change" — no gain, no loss, no setting, CR 119.1.</summary>
    LifeTotalChanges = 1 << 0,

    /// <summary>"You can't lose the game", the player half of CantLoseGame, CR 104.3.</summary>
    Lose = 1 << 1,

    /// <summary>"You can't be the target of spells or abilities your opponents control", CR 115.6.</summary>
    BeTargeted = 1 << 2,

    /// <summary>"You can't be dealt damage", the prevention-shaped prohibition, CR 120.3.</summary>
    BeDealtDamage = 1 << 3,
}

/// <summary>
/// A prohibition that applies to a player rather than to an object: "your life total can't change",
/// "you can't lose the game", "you can't be the target of spells", "prevent all damage that would be
/// dealt to you". Apply it with a StaticAbility whose Players is set, or with ApplyToPlayer for a
/// duration; the Selector-shaped To is not used for these. Several restrictions can be set at once
/// because <see cref="PlayerRestriction"/> is a flag set. CantLoseGame, CantWinGame and CantGainLife
/// stay as they are — they were already spelled that way in card files and mean exactly what they
/// say; use this record for the restrictions they do not cover.
/// </summary>
[RuleRef("614.1")]
public sealed record PlayerCant(PlayerRestriction What) : ContinuousEffect;

/// <summary>
/// "You have protection from everything", CR 702.16j: the player can't be targeted by spells or
/// abilities their opponents control, all damage that would be dealt to them is prevented, and they
/// can't be enchanted. Protection on an object is the KeywordAbility Protection with its Of filter;
/// this is the player-scoped form, which the keyword cannot carry because a player is not an object.
/// Apply it through a StaticAbility with Players set, or ApplyToPlayer for a duration.
/// </summary>
[RuleRef("702.16j")]
public sealed record PlayerProtectionFromEverything : ContinuousEffect;

public sealed record CantDrawMoreThan(PlayerRef Who, Amount PerTurn) : ContinuousEffect;

/// <summary>"Instant and sorcery spells you cast cost {1} less": positive delta is an increase.</summary>
[RuleRef("601.2f")]
public sealed record CostModifier(ObjectFilter Spells, Amount Delta, PlayerRef Who, bool GenericOnly = true) : ContinuousEffect;

[RuleRef("602.2b")]
public sealed record AbilityCostModifier(ObjectFilter Sources, Amount Delta, PlayerRef Who, AbilityKind Ability = AbilityKind.Any) : ContinuousEffect;

public sealed record NoMaxHandSize(PlayerRef Who) : ContinuousEffect;

public sealed record MaxHandSize(PlayerRef Who, Amount Size, bool Additional = false) : ContinuousEffect;

[RuleRef("305.2")]
public sealed record ExtraLandDrops(PlayerRef Who, Amount Count) : ContinuousEffect;

/// <summary>Prevention, CR 615: all when <paramref name="Amount"/> is null.</summary>
[RuleRef("615")]
public sealed record PreventDamage(Amount? Amount = null, ObjectFilter? From = null, bool? Combat = null, bool ToPlayersToo = false) : ContinuousEffect;

public sealed record DamageMultiplier(int Factor, ObjectFilter? From = null) : ContinuousEffect;

public sealed record DealsDamageAsToughness : ContinuousEffect;

public sealed record AssignDamageAsThoughUnblocked : ContinuousEffect;

/// <summary>"You may play lands from your graveyard", "you may cast spells from the top of your library".</summary>
public sealed record MayPlayFromZone(PlayerRef Who, Zone From, ObjectFilter? What = null, bool TopOfLibraryOnly = false) : ContinuousEffect;

public sealed record MayLookAtTopCard(PlayerRef Who, bool AnyTime = true) : ContinuousEffect;

public sealed record PlayWithHandRevealed(PlayerRef Who) : ContinuousEffect;

public sealed record SkipStepEffect(PlayerRef Who, Step Step) : ContinuousEffect;

[RuleRef("500.8")]
public sealed record AdditionalCombatPhase(PlayerRef Who, bool WithMainPhase = true) : ContinuousEffect;

public sealed record SpellsCostLifeInsteadOfMana(PlayerRef Who, Color Colors) : ContinuousEffect;

/// <summary>"Whenever you tap a Forest for mana, add an additional {G}."</summary>
public sealed record ManaProducesAdditional(ObjectFilter Sources, string Mana) : ContinuousEffect;

public sealed record TokensEnterWithCounters(PlayerRef Who, string Counter, Amount Count) : ContinuousEffect;

public sealed record CountersDoubled(PlayerRef Who, string? Counter = null) : ContinuousEffect;

public sealed record LifeGainDoubled(PlayerRef Who) : ContinuousEffect;

public sealed record AbilitiesTriggerTwice(PlayerRef Who, ObjectFilter? OfSources = null) : ContinuousEffect;

[RuleRef("701.19")]
public sealed record RegenerationShield : ContinuousEffect;

[RuleRef("122.1a")]
public sealed record TreatAsHavingKeywordCounter(string Counter) : ContinuousEffect;
