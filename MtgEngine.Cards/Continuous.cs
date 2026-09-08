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
[JsonDerivedType(typeof(BecomesCopyOfNamed), nameof(BecomesCopyOfNamed))]
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
[JsonDerivedType(typeof(OptionalAdditionalCost), nameof(OptionalAdditionalCost))]
[JsonDerivedType(typeof(AbilityCostModifier), nameof(AbilityCostModifier))]
[JsonDerivedType(typeof(NoMaxHandSize), nameof(NoMaxHandSize))]
[JsonDerivedType(typeof(MaxHandSize), nameof(MaxHandSize))]
[JsonDerivedType(typeof(ExtraLandDrops), nameof(ExtraLandDrops))]
[JsonDerivedType(typeof(PreventDamage), nameof(PreventDamage))]
[JsonDerivedType(typeof(DamageMultiplier), nameof(DamageMultiplier))]
[JsonDerivedType(typeof(DealsDamageAsToughness), nameof(DealsDamageAsToughness))]
[JsonDerivedType(typeof(AssignDamageAsThoughUnblocked), nameof(AssignDamageAsThoughUnblocked))]
[JsonDerivedType(typeof(DamageDoesNotReduceLifeBelow), nameof(DamageDoesNotReduceLifeBelow))]
[JsonDerivedType(typeof(MayBeginGameOnBattlefield), nameof(MayBeginGameOnBattlefield))]
[JsonDerivedType(typeof(MayPlayFromZone), nameof(MayPlayFromZone))]
[JsonDerivedType(typeof(MayCastAsThoughFlash), nameof(MayCastAsThoughFlash))]
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
[JsonDerivedType(typeof(UnsupportedContinuous), nameof(UnsupportedContinuous))]
public abstract record ContinuousEffect;

/// <summary>
/// An honest marker for a static ability's continuous effect this vocabulary cannot yet say, the
/// ContinuousEffect half of <see cref="Unsupported"/>. A static ability's slot takes a
/// ContinuousEffect and nothing else (CR 604.1), so without this record a clause the vocabulary
/// cannot express has to be dropped, and a dropped clause is a card that plays wrongly with no
/// sign of it. <paramref name="Reason"/> says what could not be said and <paramref name="Text"/>
/// keeps the printed line for the judge.
/// <para>
/// It lints as L001 exactly like Unsupported, so a file carrying one is never clean and never
/// reaches a game: the marker is a request for a record, not a way to ship a half-read card. Never
/// use it for a clause another record already says — check the vocabulary first, because a marker
/// that could have been a spelling is worse than nothing.
/// </para>
/// </summary>
[RuleRef("604.1")]
public sealed record UnsupportedContinuous(string Reason, string? Text = null) : ContinuousEffect;

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

/// <summary>
/// Layer 1: becomes a copy of another object, with the printed exceptions.
/// <para>
/// <paramref name="ExceptThisAbility"/> is the self-referential exception, CR 707.9b: "target land
/// becomes a copy of this land, except it has this ability". "This ability" is the very ability the
/// effect is part of, so the copy keeps a copy of it and can be used again — the exception is a
/// flag and never a nested ability inside <paramref name="Except"/>. Writing the ability out would
/// nest one reading of it inside another with no end to the nesting, and a reader that stopped
/// early would produce a copy that cannot copy, which is a different card.
/// </para>
/// <para>
/// The ability the flag points at is the activated or triggered ability that created this effect
/// (CR 707.9b names an ability of the object, and CR 109.5 says "this" means the object the ability
/// is on). A static ability or a spell ability has no such ability to hand back — a spell ability
/// goes to the graveyard with the card — so the flag on either is a lost sentence (L029).
/// </para>
/// </summary>
[RuleRef("707")]
public sealed record BecomesCopyOf(Selector Of, IReadOnlyList<ContinuousEffect>? Except = null, bool IsLegendary = false, bool ExceptThisAbility = false) : ContinuousEffect;

[RuleRef("613.1c")]
/// <summary>
/// "Enchanted permanent is [a printed card]", CR 707.9 with 613.1a: the object this applies to has
/// the copiable values of the card <paramref name="Name"/> names, as that card is printed and with no
/// other effect on it. <paramref name="Name"/> is the one place in this vocabulary where a card name
/// is data rather than a rule — the copied card is not in the game, so no selector can point at it —
/// and the pipeline checks the name against the corpus, so a name no card carries never reaches a
/// file. An empty name copies nothing and is refused (L036).
/// <para>
/// It is not <see cref="BecomesCopyOf"/>, whose Of names an object in the game: an Aura that makes
/// the enchanted permanent a printed card has no such object to name, and a Named filter would find
/// the wrong object or none at all. It is not ChangeText, which swaps words in a text box.
/// <paramref name="Except"/> carries the printed exceptions the way BecomesCopyOf does.
/// </para>
/// </summary>
[RuleRef("707.9")]
public sealed record BecomesCopyOfNamed(string Name, IReadOnlyList<ContinuousEffect>? Except = null) : ContinuousEffect;

public sealed record ChangeText(string From, string To) : ContinuousEffect;

/// <summary>
/// "Creatures can't attack you", "creatures can't attack unless their controller pays {2} for each
/// creature they control that's attacking you". <paramref name="Player"/> is the player being
/// protected and <paramref name="Unless"/> the board exception ("unless it's a Zombie"); neither of
/// them can carry a cost, which is why the tax is its own field.
/// <para>
/// <paramref name="UnlessPays"/> is the attack tax, CR 508.1h: a cost the attacking creature's
/// controller must pay while declaring attackers, determined and locked in as the declaration is
/// made, and the attack is simply illegal without it. A scaling tax is the cost with an
/// <see cref="Amount"/> inside it — "{2} for each creature they control that's attacking you" is a
/// PayManaCost whose generic is Times(AttackersAgainst(You), 2) — so no per-creature field is
/// needed and none exists. It is not a <see cref="CostModifier"/>, which raises the cost of casting
/// a spell, and it is not <see cref="MustAttack"/> read backwards.
/// </para>
/// <para>
/// A card prints one exception or the other, so setting both <paramref name="Unless"/> and
/// <paramref name="UnlessPays"/> is a contradiction the linter refuses (L040) — two exceptions on
/// one restriction would let an attack through by satisfying either, and no printed card says that.
/// </para>
/// </summary>
[RuleRef("508.1h")]
public sealed record CantAttack(PlayerRef? Player = null, ObjectFilter? Unless = null, Cost? UnlessPays = null) : ContinuousEffect;

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

/// <summary>
/// "Instant and sorcery spells you cast cost {1} less": positive delta is an increase.
/// <para>
/// <paramref name="OnlyColor"/> is a reduction the printed line ties to one colour — "costs {W}
/// less to cast" rather than "{1} less" (CR 601.2f, where the reduction is applied to the cost as
/// it stands). The delta is then that many mana of that colour, and a cost with no such symbol left
/// is not reduced further: a colourless spell is untouched by a {W} reduction, while a generic
/// reduction of the same size would still have paid for it. Set <paramref name="GenericOnly"/>
/// false beside it, because the two flags describe the same half of the cost and a reduction cannot
/// be generic-only and coloured at once (L030).
/// </para>
/// </summary>
[RuleRef("601.2f")]
public sealed record CostModifier(ObjectFilter Spells, Amount Delta, PlayerRef Who, bool GenericOnly = true, Color? OnlyColor = null) : ContinuousEffect;

/// <summary>
/// "Spells your opponents cast have 'as an additional cost to cast this spell, you may pay {1}',
/// and a spell paid for that way costs {2} less" — an optional additional cost this permanent
/// imposes on other spells (CR 118.8: an additional cost is paid on top of the mana cost, and an
/// optional one is a choice the caster makes as the spell is cast, CR 601.2b), with what paying it
/// buys in <paramref name="IfPaid"/>.
/// <para>
/// <paramref name="Spells"/> is the filter of spells the rider is added to and <paramref name="Who"/>
/// the caster it is offered to; <paramref name="Cost"/> is the printed additional cost, announced
/// and paid as part of casting (CR 601.2f) and not while the spell resolves.
/// <paramref name="IfPaid"/> is the continuous effect that applies to that spell only when the cost
/// was paid — normally a <see cref="CostModifier"/>, which is the reduction the printed line gates
/// on the payment. It is required (L030): an optional additional cost with nothing hanging on it is
/// a cost no caster would ever choose to pay, so a reading that drops the reward has dropped the
/// card.
/// </para>
/// <para>
/// This is not <c>Face.AdditionalCosts</c>, which is the cost printed on the card paying it, and it
/// is not <see cref="AlternativeCost"/>, which replaces the mana cost instead of adding to it. It is
/// not <see cref="MayPay"/> either: this choice is made as the other spell is cast, and MayPay's is
/// made as this card's own ability resolves.
/// </para>
/// </summary>
[RuleRef("118.8")]
public sealed record OptionalAdditionalCost(ObjectFilter Spells, Cost Cost, PlayerRef Who, ContinuousEffect? IfPaid = null) : ContinuousEffect;

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

/// <summary>
/// "Damage that would reduce your life total to less than 1 reduces it to 1 instead": a floor under
/// a player's life total against damage, CR 120.4 (damage dealt to a player causes that player to
/// lose that much life) with CR 119.3 (the life loss itself). It applies to the players a
/// StaticAbility's <c>Players</c> or an ApplyToPlayer names, and it replaces only the damage event:
/// the amount of damage is reduced so that the life total lands on <paramref name="Floor"/> and no
/// lower, and damage that leaves the total at or above the floor is dealt unchanged.
/// <para>
/// It is not <see cref="PreventDamage"/>, which prevents a fixed amount or all of it whatever the
/// life total is (CR 615), and it is not <see cref="CantLoseGame"/>, which lets the total fall and
/// only stops the loss: a card that prints both sentences prints two abilities, and each is written
/// as its own record. It is also not a life loss floor — <paramref name="Floor"/> bites on damage
/// alone, so a cost that pays life or an effect that says "lose life" is untouched, exactly as the
/// printed line says.
/// </para>
/// </summary>
[RuleRef("120.4")]
public sealed record DamageDoesNotReduceLifeBelow(Amount Floor) : ContinuousEffect;

/// <summary>
/// "If this card is in your opening hand, you may begin the game with it on the battlefield": the
/// one kind of ability that functions before the game begins, CR 103.6. The StaticAbility that
/// carries it names <c>Where: Zone.Hand</c>, because the hand is the zone the ability works from and
/// a battlefield-scoped ability would never be read (L027).
/// <para>
/// <paramref name="OnlyIf"/> is the printed rider some of these carry — "and you're not the starting
/// player" — read as the game is set up and never again. <paramref name="WithCounters"/> is "with a
/// luck counter on it", the counters the permanent begins the game with, and
/// <paramref name="IfYouDo"/> is the cost the offer carries: "if you do, exile a card from your
/// hand". Leave each out when the card does not print it.
/// </para>
/// <para>
/// The permission is one record and never a trigger: nothing has happened yet for a trigger to
/// watch, there is no turn and no stack (CR 103.6), so an EntersBattlefield or AtStep reading of
/// this line puts the card on the battlefield at a moment the rules do not offer.
/// </para>
/// </summary>
[RuleRef("103.6")]
public sealed record MayBeginGameOnBattlefield(Condition? OnlyIf = null, IReadOnlyList<CounterSpec>? WithCounters = null, Effect? IfYouDo = null) : ContinuousEffect;

public sealed record DealsDamageAsToughness : ContinuousEffect;

public sealed record AssignDamageAsThoughUnblocked : ContinuousEffect;

/// <summary>
/// "You may play lands from your graveyard", "you may cast spells from the top of your library".
/// With WithoutPayingManaCost, "you may cast it this turn without paying its mana cost" (CR 118.9):
/// the permission also waives the mana cost of the cast it allows.
/// <para>
/// With <paramref name="PayLifeEqualToManaValueInstead"/>, "if you cast a spell this way, pay life
/// equal to its mana value rather than pay its mana cost": an alternative cost (CR 118.9) that the
/// permission itself carries, paid at CR 601.2f in place of the mana cost, and only by a cast this
/// permission allowed — a spell the player casts from hand pays as printed. The two sentences are
/// one record, and this flag is their one spelling: never a second StaticAbility for the second
/// sentence, and never SpellsCostLifeInsteadOfMana, which is every spell of a colour from any zone.
/// A land played this way pays nothing either way. The two flags are two different alternative
/// costs and a card prints one, so both set is a contradiction the linter refuses (L021).
/// </para>
/// </summary>
[RuleRef("118.9")]
public sealed record MayPlayFromZone(PlayerRef Who, Zone From, ObjectFilter? What = null, bool TopOfLibraryOnly = false, bool WithoutPayingManaCost = false, bool PayLifeEqualToManaValueInstead = false) : ContinuousEffect;

/// <summary>
/// "You may cast green creature spells as though they had flash", CR 702.8: a timing permission
/// granted to a player, not an ability granted to a card. <paramref name="Who"/> is the player it
/// is granted to and <paramref name="What"/> describes the spells they may cast that way — the
/// filter is read as the spell is cast, so it sees the card in whatever zone the player is casting
/// it from.
/// <para>
/// This is not <see cref="GrantKeyword"/> with Flash, which needs objects on the battlefield or in
/// a zone a continuous effect reaches and cannot describe "spells you cast" at all, and it is not
/// <see cref="MayPlayFromZone"/>, which is a permission to cast from somewhere the player otherwise
/// could not — the two are different halves of casting and a card that prints both prints two
/// sentences. It grants timing only: the spell still costs what it costs, and the cast is still a
/// cast (CR 601), so anything watching casts sees it.
/// </para>
/// <para>
/// A land is played and not cast (CR 305.1), so a filter that admits only lands says nothing here
/// and is refused (L042); "you may play lands as though..." is an extra land drop or a play
/// permission, never this record.
/// </para>
/// </summary>
[RuleRef("702.8")]
public sealed record MayCastAsThoughFlash(PlayerRef Who, ObjectFilter? What = null) : ContinuousEffect;

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
