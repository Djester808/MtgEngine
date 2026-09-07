using System.Text.Json.Serialization;

namespace MtgEngine.Cards;

/// <summary>
/// Which objects an instruction acts on. A selector names the objects; an <see cref="ObjectFilter"/>
/// describes them. Targets are selectors, and so are "each creature", "this permanent" and
/// "the exiled card".
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Self), nameof(Self))]
[JsonDerivedType(typeof(Target), nameof(Target))]
[JsonDerivedType(typeof(AnyTarget), nameof(AnyTarget))]
[JsonDerivedType(typeof(Each), nameof(Each))]
[JsonDerivedType(typeof(Choose), nameof(Choose))]
[JsonDerivedType(typeof(Attached), nameof(Attached))]
[JsonDerivedType(typeof(Attachments), nameof(Attachments))]
[JsonDerivedType(typeof(Triggering), nameof(Triggering))]
[JsonDerivedType(typeof(Remembered), nameof(Remembered))]
[JsonDerivedType(typeof(Top), nameof(Top))]
[JsonDerivedType(typeof(RandomFrom), nameof(RandomFrom))]
[JsonDerivedType(typeof(SourceOfDamage), nameof(SourceOfDamage))]
[JsonDerivedType(typeof(ThisSpellsTargets), nameof(ThisSpellsTargets))]
[JsonDerivedType(typeof(Commander), nameof(Commander))]
[JsonDerivedType(typeof(Union), nameof(Union))]
public abstract record Selector;

/// <summary>This object: the permanent with the ability, or the spell being cast.</summary>
public sealed record Self : Selector;

/// <summary>"target creature", "up to two target players' creatures". <paramref name="Key"/> lets later steps refer back.</summary>
[RuleRef("115")]
public sealed record Target(ObjectFilter Filter, Amount? Count = null, bool UpTo = false, string? Key = null, bool Distinct = true) : Selector;

/// <summary>
/// "Any target", CR 115.4: exactly one target chosen from among every creature, player,
/// planeswalker and battle. This is the whole phrase in one selector, and it is the only spelling
/// of it. Do not write it as a Target whose filter names Creature, Planeswalker and Battle beside
/// a toPlayer, and do not write it as an open filter beside a toPlayer: the first cannot say the
/// player half and the second lets the damage hit a land.
///
/// On DealDamage it goes in To, and ToPlayer is then left out — To of AnyTarget already means
/// "whichever of the four was chosen". The chosen target is announced as the spell or ability is
/// put on the stack (CR 601.2c): an object target rides in the command's Targets and a player
/// target in its TargetPlayers, so the two halves are one choice at cast time and the resolving
/// effect deals its damage to whichever half the stack entry holds.
///
/// Count, UpTo and Key are here for the same reason Target carries them: "any number of target
/// creatures" has its counterpart in "up to one target", divided damage names several targets
/// (CR 601.2d), and a later step refers back by Key. Count absent means one.
/// </summary>
[RuleRef("115.4")]
public sealed record AnyTarget(Amount? Count = null, bool UpTo = false, string? Key = null) : Selector;

/// <summary>"each creature", "all artifacts": every object matching the filter, no targeting.</summary>
public sealed record Each(ObjectFilter Filter) : Selector;

/// <summary>A non-targeted choice: "choose a creature you control". The chooser picks when the effect resolves.</summary>
public sealed record Choose(ObjectFilter Filter, PlayerRef Chooser, Amount? Count = null, bool UpTo = false, string? Key = null) : Selector;

/// <summary>The object this Aura, Equipment or Fortification is attached to: "enchanted creature", "equipped creature".</summary>
[RuleRef("301.5")]
public sealed record Attached : Selector;

/// <summary>Objects attached to this one, optionally filtered.</summary>
public sealed record Attachments(ObjectFilter? Filter = null) : Selector;

/// <summary>The object the trigger was about, or the other object involved: "that creature", "it", "the blocked creature".</summary>
public sealed record Triggering(TriggerRole Role = TriggerRole.Subject) : Selector;

/// <summary>Objects an earlier step produced and remembered under a key: "the exiled card", "those tokens".</summary>
public sealed record Remembered(string Key) : Selector;

/// <summary>"the top card of your library", "the top three cards of target player's library".</summary>
public sealed record Top(PlayerRef Whose, Amount? Count = null, Zone Zone = Zone.Library) : Selector;

/// <summary>"a card at random from your hand".</summary>
public sealed record RandomFrom(ObjectFilter Filter, Amount? Count = null) : Selector;

/// <summary>The source of the damage a trigger or replacement is about.</summary>
public sealed record SourceOfDamage : Selector;

/// <summary>The targets this spell or ability chose, for effects that refer back to them collectively.</summary>
public sealed record ThisSpellsTargets : Selector;

/// <summary>A player's commander, CR 903.</summary>
[RuleRef("903")]
public sealed record Commander(PlayerRef Whose) : Selector;

/// <summary>Two selections taken together.</summary>
public sealed record Union(IReadOnlyList<Selector> Of) : Selector;

/// <summary>
/// A description of objects. Every field is optional and every set field must hold. This is
/// the noun phrase of the game: "another nontoken creature you control with power 4 or greater"
/// is one filter. Anything the fields cannot say goes in <see cref="Also"/>.
/// Because the set fields are joined with "and", the fixed fields cannot say "or": "attacking or
/// blocking creature" is not Attacking plus Blocking, which would match nothing. That one disjunction
/// is common enough to have its own field — InCombat, true for a creature that is an attacking or a
/// blocking creature (CR 506.2, 506.4) and false for one that is neither. Any other disjunction is
/// written with AnyOf in <see cref="Also"/>.
/// </summary>
public sealed record ObjectFilter(
    Zone? In = null,
    CardType? Types = null,
    CardType? AnyTypes = null,
    CardType? NotTypes = null,
    Supertype? Supertypes = null,
    Supertype? NotSupertypes = null,
    IReadOnlyList<string>? Subtypes = null,
    IReadOnlyList<string>? NotSubtypes = null,
    PlayerRef? ControlledBy = null,
    PlayerRef? OwnedBy = null,
    Color? Colors = null,
    Color? AnyColor = null,
    Color? NotColors = null,
    bool? Colorless = null,
    bool? Multicolored = null,
    bool? Monocolored = null,
    Comparison? Power = null,
    Comparison? Toughness = null,
    Comparison? ManaValue = null,
    Comparison? Loyalty = null,
    bool? Tapped = null,
    bool? Attacking = null,
    bool? Blocking = null,
    bool? Blocked = null,
    bool? InCombat = null,
    bool? Token = null,
    bool? Other = null,
    bool? FaceDown = null,
    bool? Historic = null,
    bool? Outlaw = null,
    bool? Permanent = null,
    bool? Spell = null,
    string? Named = null,
    KeywordKind? Has = null,
    string? WithCounter = null,
    bool? WithAnyCounter = null,
    bool? IsCommander = null,
    Predicate? Also = null)
{
    public static ObjectFilter Creature => new(Types: CardType.Creature);

    public static ObjectFilter Any => new();
}

/// <summary>The long tail of object descriptions the fixed fields of <see cref="ObjectFilter"/> cannot express.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SharesColorWith), nameof(SharesColorWith))]
[JsonDerivedType(typeof(SharesTypeWith), nameof(SharesTypeWith))]
[JsonDerivedType(typeof(SharesNameWith), nameof(SharesNameWith))]
[JsonDerivedType(typeof(AttachedTo), nameof(AttachedTo))]
[JsonDerivedType(typeof(IsAttached), nameof(IsAttached))]
[JsonDerivedType(typeof(EnteredThisTurn), nameof(EnteredThisTurn))]
[JsonDerivedType(typeof(CastThisTurn), nameof(CastThisTurn))]
[JsonDerivedType(typeof(DealtDamageThisTurn), nameof(DealtDamageThisTurn))]
[JsonDerivedType(typeof(WasDealtDamageThisTurn), nameof(WasDealtDamageThisTurn))]
[JsonDerivedType(typeof(TargetedBy), nameof(TargetedBy))]
[JsonDerivedType(typeof(IsChosenName), nameof(IsChosenName))]
[JsonDerivedType(typeof(IsChosenType), nameof(IsChosenType))]
[JsonDerivedType(typeof(IsChosenColor), nameof(IsChosenColor))]
[JsonDerivedType(typeof(CanBlock), nameof(CanBlock))]
[JsonDerivedType(typeof(CouldBlock), nameof(CouldBlock))]
[JsonDerivedType(typeof(ManaValueParity), nameof(ManaValueParity))]
[JsonDerivedType(typeof(WasKicked), nameof(WasKicked))]
[JsonDerivedType(typeof(HasSummoningSickness), nameof(HasSummoningSickness))]
[JsonDerivedType(typeof(IsSuspected), nameof(IsSuspected))]
[JsonDerivedType(typeof(IsGoaded), nameof(IsGoaded))]
[JsonDerivedType(typeof(IsRingBearer), nameof(IsRingBearer))]
[JsonDerivedType(typeof(IsHarnessed), nameof(IsHarnessed))]
[JsonDerivedType(typeof(IsPrepared), nameof(IsPrepared))]
[JsonDerivedType(typeof(IsTransformed), nameof(IsTransformed))]
[JsonDerivedType(typeof(IsCopy), nameof(IsCopy))]
[JsonDerivedType(typeof(IsThisObject), nameof(IsThisObject))]
[JsonDerivedType(typeof(HasAbilityOfKind), nameof(HasAbilityOfKind))]
[JsonDerivedType(typeof(ProducesMana), nameof(ProducesMana))]
[JsonDerivedType(typeof(NotPredicate), nameof(NotPredicate))]
[JsonDerivedType(typeof(AllOf), nameof(AllOf))]
[JsonDerivedType(typeof(AnyOf), nameof(AnyOf))]
public abstract record Predicate;

public sealed record SharesColorWith(Selector With) : Predicate;

/// <summary>
/// Shares a card type — or, with <paramref name="CreatureTypeOnly"/>, a creature type — with the
/// object <paramref name="With"/> names. This is how a relation between two objects the same effect
/// touched is written, and it is the answer to the shape translators reach for a new record for:
/// "search your library for two basic land cards that share a land type" is a Sequence whose first
/// step searches and remembers the first card, and whose second step searches for one that shares a
/// type with it —
/// <c>Sequence([Search(basicLand, Battlefield, RememberAs: "first"),
/// Search(new ObjectFilter(Types: CardType.Land, Supertypes: Supertype.Basic,
/// Also: new SharesTypeWith(new Remembered("first"), CreatureTypeOnly: false)), Zone.Battlefield)])</c>.
/// Remember plus this predicate expresses every "that share a ..." and "with the same ..." relation;
/// SharesColorWith and SharesNameWith are the same pattern for colour and name.
/// </summary>
public sealed record SharesTypeWith(Selector With, bool CreatureTypeOnly = false) : Predicate;

public sealed record SharesNameWith(Selector With) : Predicate;

/// <summary>Attached to the given object: "Auras attached to it".</summary>
public sealed record AttachedTo(Selector To) : Predicate;

/// <summary>Attached to anything at all, or unattached when false.</summary>
public sealed record IsAttached(bool Value = true) : Predicate;

public sealed record EnteredThisTurn : Predicate;

public sealed record CastThisTurn : Predicate;

public sealed record DealtDamageThisTurn(PlayerRef? To = null) : Predicate;

public sealed record WasDealtDamageThisTurn : Predicate;

/// <summary>Currently targeted by the given object, typically <see cref="Self"/>.</summary>
public sealed record TargetedBy(Selector By) : Predicate;

public sealed record IsChosenName(string Key) : Predicate;

public sealed record IsChosenType(string Key) : Predicate;

public sealed record IsChosenColor(string Key) : Predicate;

/// <summary>Able to block the given attacker right now.</summary>
public sealed record CanBlock(Selector Attacker) : Predicate;

/// <summary>Could block the given creature if it were attacking, ignoring whether combat is happening.</summary>
public sealed record CouldBlock(Selector Attacker) : Predicate;

public sealed record ManaValueParity(bool Odd) : Predicate;

[RuleRef("702.33")]
public sealed record WasKicked : Predicate;

[RuleRef("302.6")]
public sealed record HasSummoningSickness(bool Value = true) : Predicate;

[RuleRef("701.60")]
public sealed record IsSuspected : Predicate;

[RuleRef("701.15")]
public sealed record IsGoaded(PlayerRef? By = null) : Predicate;

[RuleRef("701.54")]
public sealed record IsRingBearer : Predicate;

[RuleRef("701.64")]
public sealed record IsHarnessed : Predicate;

/// <summary>
/// Carries the Prepared designation, CR 702.180: "prepared creatures you control", "if it's
/// prepared". The designation is put on by the Prepare effect and taken off by Unprepare; it is not
/// a counter, so WithCounter does not see it.
/// </summary>
[RuleRef("702.180")]
public sealed record IsPrepared : Predicate;

public sealed record IsTransformed : Predicate;

public sealed record IsCopy : Predicate;

/// <summary>
/// The object this ability is printed on, and nothing else. "This" in a card's text always means
/// the object the ability is on (CR 109.5), so a trigger that is about its own source says it with
/// this predicate: "whenever this creature attacks" is Attacks with ObjectFilter(Also: IsThisObject),
/// and "whenever this creature dies" is Dies with the same filter (CR 603.2 — a trigger's event is
/// matched against its own source like any other object). Do not confuse it with Other, which means
/// "another" and excludes this object, or with an unmarked filter, which matches every object it
/// describes including this one. The selector Self is the same idea on the effect side: use Self to
/// act on this object, IsThisObject to describe it inside a filter.
/// </summary>
[RuleRef("109.5")]
public sealed record IsThisObject : Predicate;

/// <summary>Has an ability of the given kind: "a creature with a mana ability", "with an activated ability".</summary>
public sealed record HasAbilityOfKind(AbilityKind Ability) : Predicate;

/// <summary>A source that could produce mana of the given colors.</summary>
public sealed record ProducesMana(Color Colors) : Predicate;

public sealed record NotPredicate(Predicate Of) : Predicate;

public sealed record AllOf(IReadOnlyList<Predicate> Of) : Predicate;

public sealed record AnyOf(IReadOnlyList<Predicate> Of) : Predicate;
