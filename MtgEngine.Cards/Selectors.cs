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

/// <summary>
/// "target creature", "up to two target players' creatures". <paramref name="Key"/> lets later
/// steps refer back.
/// <para>
/// <paramref name="Distinct"/> is CR 601.2c's default and is why it is <b>true</b> unless a card
/// says otherwise: the same object can't be chosen for two of one spell's targets, so a Target with
/// a <paramref name="Count"/> above one names that many different objects. Set it false only for
/// the printed line that lifts the rule — "any number of targets, which may be the same" and the
/// divided-damage wording that lets one creature take two shares — and never as a way of writing
/// "any number of target creature cards that each have a different mana value", which is a
/// restriction on the <b>cards</b> and belongs in the filter, not here: distinctness of the objects
/// chosen and difference of a characteristic they have are two different sentences, and this field
/// says only the first.
/// </para>
/// </summary>
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
///
/// <paramref name="Other"/> is the printed word in "any other target": the object with this ability
/// can't be chosen, and every other creature, player, planeswalker and battle can. It is a
/// restriction on the choice made at CR 601.2c and not a filter — "any target" admits players and
/// battles, which no ObjectFilter reaches, so a reading that writes the exclusion as a filter over
/// objects quietly drops the other three halves of the phrase. Leave it false for a plain
/// "any target".
/// </summary>
[RuleRef("115.4")]
public sealed record AnyTarget(Amount? Count = null, bool UpTo = false, string? Key = null, bool Other = false) : Selector;

/// <summary>"each creature", "all artifacts": every object matching the filter, no targeting.</summary>
public sealed record Each(ObjectFilter Filter) : Selector;

/// <summary>
/// A non-targeted choice: "choose a creature you control". The chooser picks when the effect
/// resolves. <paramref name="Among"/> is "from among them": the candidates are the objects that
/// selector names — a Remembered key from an earlier reveal, look or exile — wherever they are,
/// and <paramref name="Filter"/> only narrows them ("permanent cards with mana value X or less from
/// among them"). With Among set the filter's In is not read, because the candidates' zone is their
/// own; writing one beside it is L022. This is the one spelling of a choice among remembered
/// objects: never an IsRemembered predicate in the filter, whose zone would default to the
/// battlefield and match none of the revealed cards. "Any number" is UpTo with no Count.
/// </summary>
[RuleRef("700.2")]
public sealed record Choose(ObjectFilter Filter, PlayerRef Chooser, Amount? Count = null, bool UpTo = false, string? Key = null, Selector? Among = null) : Selector;

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
/// The fields are joined with "and", except the two subtype lists, which are "any of": Subtypes
/// ["Forest", "Island"] is "a Forest or Island card" (CR 205.3, an object has each of its subtypes),
/// and NotSubtypes excludes an object that has any listed one; "a Forest Island" (both) is two
/// nested filters, not one list. The fixed fields otherwise cannot say "or": "attacking or
/// blocking creature" is not Attacking plus Blocking, which would match nothing. That one disjunction
/// is common enough to have its own field — InCombat, true for a creature that is an attacking or a
/// blocking creature (CR 506.2, 506.4) and false for one that is neither. Any other disjunction is
/// written with AnyOf in <see cref="Also"/>. "Attacking or blocking creature" is InCombat and has no
/// second spelling: there is no AttackingOrBlocking field, and writing Attacking beside Blocking
/// matches nothing.
/// <para>
/// HasBasicLandType is "a land card with a basic land type", CR 205.3i: true when the object has
/// any of Forest, Island, Mountain, Plains or Swamp among its subtypes, whatever else it also has,
/// and false for a nonbasic land with none of them. It is not Supertype.Basic — a shockland and a
/// dual land have a basic land type and are not basic — and it is not a Subtypes list, which is
/// joined with "and" and so cannot say "any of the five".
/// </para>
/// <para>
/// ManaCostIs is the printed mana cost read as a whole string, CR 202.1: the list is "any of", and
/// an object matches when its printed cost is one of the listed symbol strings exactly as printed —
/// ["{0}", "{1}"] is "with mana value 0 or 1" said the way a card that cares about the printed cost
/// says it. It is not ManaValue, which counts the cost and so admits "{W}" for 1 and cannot tell
/// "{0}" from a land's absent cost; use ManaValue whenever the card prints a number, and this only
/// where the printed symbols themselves are named. An object with no printed mana cost matches no
/// entry, and an empty list matches nothing at all rather than everything.
/// </para>
/// <para>
/// NotInCommanderIdentityColors is "colors not in your commander's color identity", CR 903.4: true
/// admits an object of a colour outside that identity and false admits one wholly inside it. It is
/// CastFrom and NotCastFrom are the zone a spell was cast from, CR 601.3 read as a description of the
/// spell rather than of the caster: "players cannot cast spells from anywhere other than their hands"
/// is a CantCastSpells whose Matching filter has Spell true and NotCastFrom Hand. Set one or the
/// other, never both — a card names the zone it allows or the one it forbids, and both together
/// admits nothing (L038) — and only on a filter that describes a spell: a permanent was never cast
/// from anywhere the game still remembers. It is not the CastFromZone condition, which asks where
/// this card was cast from and can say nothing about anyone else spell.
/// </para>
/// <para>
/// the narrow spelling of the one colour-set reference the pilot asked for and takes no colours of
/// its own, because the set is the game's and not the card's. Never write it as a Colors or
/// NotColors list, which would fix at translation time a set that depends on the deck.
/// </para>
/// <para>
/// <c>NotHas</c> is the printed negation, "creatures without flying" (CR 702.1, and 702.9 for the
/// keyword that prints it most): the objects this filter admits are the ones that do <b>not</b>
/// have that keyword ability, whether it is printed on them or granted. It is the mirror of
/// <c>Has</c> and never the same field with anything turned around, so a card that says "without"
/// is this member and a card that says "with" is that one; setting both to the same keyword admits
/// nothing (L045). It is not <c>NotSubtypes</c>, which is a creature type, and not a Predicate: the
/// absence of a keyword is a fixed field because the presence of one already is.
/// </para>
/// <para>
/// <c>InAnyOf</c> is the one filter a sentence naming more than one zone gets: "creature cards in
/// graveyards and libraries" is <c>InAnyOf: [Graveyard, Library]</c>, admitting an object in any of
/// them (CR 400.1 — each zone is its own place, and an object is in exactly one). It is the plural
/// of <c>In</c> and never beside it: a filter that says both a zone and a list of zones states two
/// different memberships and only one of them would be read (L047). A single-entry list is a plain
/// <c>In</c> written the long way and is refused for the same reason, and a filter with neither
/// still means the battlefield wherever a filter defaults there — an empty list is not "any zone".
/// </para>
/// </summary>
[RuleRef("202.1")]
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
    bool? HasBasicLandType = null,
    IReadOnlyList<string>? ManaCostIs = null,
    bool? NotInCommanderIdentityColors = null,
    Zone? CastFrom = null,
    Zone? NotCastFrom = null,
    Predicate? Also = null,
    KeywordKind? NotHas = null,
    IReadOnlyList<Zone>? InAnyOf = null)
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
[JsonDerivedType(typeof(IsRemembered), nameof(IsRemembered))]
[JsonDerivedType(typeof(IsOneOf), nameof(IsOneOf))]
[JsonDerivedType(typeof(DrawnThisTurn), nameof(DrawnThisTurn))]
[JsonDerivedType(typeof(EnteredThisTurn), nameof(EnteredThisTurn))]
[JsonDerivedType(typeof(CastThisTurn), nameof(CastThisTurn))]
[JsonDerivedType(typeof(DealtDamageThisTurn), nameof(DealtDamageThisTurn))]
[JsonDerivedType(typeof(WasDealtDamageThisTurn), nameof(WasDealtDamageThisTurn))]
[JsonDerivedType(typeof(TargetedBy), nameof(TargetedBy))]
[JsonDerivedType(typeof(Targets), nameof(Targets))]
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
[JsonDerivedType(typeof(IsModified), nameof(IsModified))]
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

/// <summary>
/// The candidate is attached to what <paramref name="To"/> names, CR 301.5c. The direction is
/// candidate-first and never the other way round: the object being described is the attachment, and
/// <paramref name="To"/> is what it hangs on. <c>AttachedTo(new Self())</c> therefore means "an Aura
/// or Equipment attached to this permanent", which is what "Auras attached to it" says.
/// <para>
/// It is not how "equipped creature" is written. That phrase names the creature this Equipment is
/// attached to — the other end of the same link — and its spelling is the selector
/// <see cref="Attached"/>, with no predicate at all. A filter of
/// <c>ObjectFilter(Also: AttachedTo(Self))</c> matches the Equipment's own Auras and matches the
/// equipped creature never, so a card written that way silently affects nothing.
/// </para>
/// </summary>
[RuleRef("301.5c")]
public sealed record AttachedTo(Selector To) : Predicate;

/// <summary>
/// The candidate is one of the objects an earlier step remembered under <paramref name="Key"/>,
/// CR 603.10: "that creature", "the exiled card", said inside a filter rather than as a selector.
/// The <see cref="Remembered"/> selector names those objects to act on them; this predicate
/// describes them, so a filter can require an object to be the remembered one and hold its other
/// conditions at the same time — "prevent all damage that would be dealt by the chosen creature" is
/// a filter of <c>Also: IsRemembered(key)</c>, which no selector position can say.
/// <para>
/// It reads a key another step produced and produces none itself, so the key must be remembered on
/// the same face or the file does not lint (L003). Do not use it where a bare Remembered selector
/// already fits: one spelling per idea, and a filter that names exactly one object and nothing else
/// is that selector.
/// </para>
/// </summary>
[RuleRef("603.10")]
public sealed record IsRemembered(string Key) : Predicate;

/// <summary>
/// The candidate is one of the objects <paramref name="Of"/> names: a selector said inside a
/// filter. "Whenever equipped creature dies" is Dies with a filter of <c>Also: IsOneOf(Attached)</c>
/// (CR 301.5c names the equipped creature from the Equipment's side), and "whenever that creature
/// attacks" inside a delayed trigger is IsOneOf(Triggering). It is the general form of IsRemembered,
/// which is IsOneOf(Remembered(key)) and stays the spelling for a remembered key; use this for the
/// selectors that have no predicate of their own — Attached, Triggering, Commander, Top.
/// <para>
/// It is not AttachedTo(Self), which runs the other way: that predicate admits the Auras and
/// Equipment hanging on this permanent, never the creature this permanent hangs on, so a trigger
/// written with it fires for the wrong objects.
/// </para>
/// </summary>
[RuleRef("301.5c")]
public sealed record IsOneOf(Selector Of) : Predicate;

/// <summary>Attached to anything at all, or unattached when false.</summary>
public sealed record IsAttached(bool Value = true) : Predicate;

/// <summary>
/// A card its owner drew this turn, CR 121.1: "choose two cards in your hand that you drew this
/// turn". A draw is a game event and the game remembers which cards were drawn, so this describes
/// a card in a hand by how it got there and by nothing else.
/// <para>
/// It is not <see cref="EnteredThisTurn"/>, which is a permanent entering the battlefield
/// (CR 603.6a) and says nothing about a hand; and it is not a Remembered key, because the cards
/// were drawn by another turn's step or by another ability, so no step of this card produced them.
/// The filter it rides on carries the hand itself — <c>In: Zone.Hand</c> with a
/// <c>ControlledBy</c>/<c>OwnedBy</c> of the player whose hand is meant — and this predicate only
/// narrows that hand to the cards drawn this turn.
/// </para>
/// </summary>
[RuleRef("121.1")]
public sealed record DrawnThisTurn : Predicate;

public sealed record EnteredThisTurn : Predicate;

public sealed record CastThisTurn : Predicate;

public sealed record DealtDamageThisTurn(PlayerRef? To = null) : Predicate;

public sealed record WasDealtDamageThisTurn : Predicate;

/// <summary>Currently targeted by the given object, typically <see cref="Self"/>.</summary>
public sealed record TargetedBy(Selector By) : Predicate;

/// <summary>
/// "target spell that targets a permanent you control", CR 115.1: the candidate is a spell or ability
/// on the stack that has chosen a target <paramref name="Of"/> admits. It reads the targets the
/// candidate announced (CR 601.2c), so a spell with no targets matches nothing here, and a spell
/// whose only target has since become illegal still targeted it.
/// <para>
/// It runs the opposite way from <see cref="TargetedBy"/>, which admits the object a spell points at;
/// this admits the spell doing the pointing, and a filter that means one and writes the other matches
/// the wrong half of the same pair. The filter it rides on carries the spell itself — Spell true,
/// with In Stack where the card names the zone — and a filter that says this of a permanent describes
/// an object that cannot target (L037).
/// </para>
/// </summary>
[RuleRef("115.1")]
public sealed record Targets(ObjectFilter Of) : Predicate;

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
/// "modified creatures you control", "if it's modified", CR 700.9: a permanent is modified if it
/// has one or more counters on it (CR 122), if it is equipped (CR 301.5), or if it is enchanted by
/// an Aura **its own controller controls** (CR 303.4). Any one of the three is enough, and the
/// third is the half a reading loses: an opponent's Aura on your creature does not modify it.
/// <para>
/// It is one predicate and not three ORed conditions, because the rulebook gives the word one
/// definition and a card that prints it means all of it: <c>WithCounter</c> alone misses the
/// Equipment and the Aura, and an <see cref="IsAttached"/> reading asks whether this permanent is
/// attached to something rather than whether something is attached to it. A counter of any kind
/// counts, so this is never <c>WithCounter</c> with a kind. It describes a permanent on the
/// battlefield; a card in a graveyard is modified by nothing.
/// </para>
/// </summary>
[RuleRef("700.9")]
public sealed record IsModified : Predicate;

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
