using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>
/// The source of a trigger, and what the game knows about abilities beside it.
/// </summary>
/// <remarks>
/// A predicate used to be handed the <see cref="GameObject"/> alone, which meant it could not
/// call <see cref="Characteristics.Of"/> and had to answer every question about a creature from
/// its <em>printed</em> card. That is the wrong answer under CR 613: "a creature with flying"
/// means one that has flying now, however it got it, and a creature granted flying was invisible
/// to every trigger that asked.
/// <para>
/// A struct rather than a record class because a predicate runs once per event per trigger, and
/// the corpus checks run over a million of them. The conversion to <see cref="GameObject"/> is
/// implicit so that the ninety-odd predicates that only ever wanted the object read exactly as
/// they did before - the abilities are there for the ones that ask.
/// </para>
/// </remarks>
public readonly record struct TriggerSource(GameObject Subject, IAbilitySource Abilities)
{
    public ObjectId Id => Subject.Id;

    public CardDefinition Card => Subject.Card;

    public Guid ControllerId => Subject.ControllerId;

    public Guid OwnerId => Subject.OwnerId;

    public Zone Zone => Subject.Zone;

    public PermanentState? Permanent => Subject.Permanent;

    public bool WasBlitzed => Subject.WasBlitzed;

    public static implicit operator GameObject(TriggerSource source) => source.Subject;

    /// <summary>The object as it is now, after the layers (CR 613).</summary>
    public ComputedCharacteristics Now(GameState state) =>
        Characteristics.Of(state, Abilities, Subject);
}

/// <summary>
/// A triggered ability a card has: a condition that watches events, and text (CR 603.1).
/// </summary>
/// <remarks>
/// The condition is a delegate and therefore lives out here, never in <see cref="GameState"/>.
/// State has to fold from a log and compare by value; a captured closure does neither. What the
/// state remembers is that <em>this ability of this object</em> is waiting to go on the stack —
/// see <see cref="PendingTrigger"/> — and the definition is looked up again by id.
/// </remarks>
public sealed record TriggeredAbilityDefinition
{
    /// <summary>
    /// The chapter number this ability is, for a Saga's chapter ability (CR 714.2a).
    /// </summary>
    /// <remarks>
    /// Recorded rather than left implicit in the trigger predicate, because two other rules have
    /// to ask about it from outside the ability: CR 714.2d needs the greatest chapter number a
    /// Saga has in order to know its final chapter, and CR 714.4 sacrifices the Saga once its
    /// lore counters reach that number. Neither question can be answered by a predicate, which
    /// only ever says yes or no to one event.
    /// <para>
    /// Null on every ability that is not a chapter, which is nearly all of them.
    /// </para>
    /// </remarks>
    public int? Chapter { get; init; }

    /// <summary>
    /// Whether this is a Class's "when this Class becomes level N" trigger (CR 716.2a).
    /// </summary>
    /// <remarks>
    /// It is printed inside the section its own level switches on, so the level gate that section
    /// wraps around everything else would refuse it: a trigger reads the state as it was before
    /// the event, and before the event the Class is still on the level below. Flagged rather than
    /// detected by its text downstream, because the compiler is the only place that knows which
    /// section a trigger came out of.
    /// </remarks>
    public bool AnnouncesLevel { get; init; }

    /// <summary>
    /// Whether this is a Room's "when you unlock this door" trigger (CR 709.5f).
    /// </summary>
    /// <remarks>
    /// Exactly the same exception as <see cref="AnnouncesLevel"/>, one mechanic along: it is
    /// printed behind the door it fires on, and a trigger reads the state as it was before the
    /// event - where that door is still shut. Gated like the rest of its half, it would refuse
    /// the one event it exists for.
    /// </remarks>
    public bool OpensDoor { get; init; }

    /// <summary>Stable within its card, so a pending trigger can name it across a replay.</summary>
    public required string Id { get; init; }

    /// <summary>The ability's text, which is all it has on the stack (CR 405.4).</summary>
    public required string Text { get; init; }

    /// <summary>
    /// Whether this event triggers the ability (CR 603.2). The source is the object as it was
    /// when the event happened, and what the game knows about abilities alongside it.
    /// </summary>
    public required Func<GameEvent, GameState, TriggerSource, bool> Triggers { get; init; }

    /// <summary>
    /// A condition that triggers the ability by being true, rather than by anything happening
    /// (CR 603.8).
    /// </summary>
    /// <remarks>
    /// "When you control no Islands, sacrifice this creature" watches no event: nothing has to
    /// happen for it to be true, and it is just as true if the last Island left the battlefield
    /// three turns ago. So it is asked wherever state-based actions are checked, which is
    /// wherever a player would receive priority (CR 704.3).
    /// <para>
    /// A state trigger fires <em>once</em> while the condition holds, and not again until the
    /// condition has become false and then true again (CR 603.8). Without that it would trigger
    /// every time the game settled, which for a condition its own resolution does not clear is an
    /// endless loop rather than a card.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Takes the ability source as well as the state, because a condition about the board is
    /// usually a condition about a <em>characteristic</em> - "a creature with power 4 or
    /// greater" - and a characteristic cannot be computed without knowing what abilities are on
    /// the battlefield. Asked without one, every anthem in the game is invisible and the
    /// condition answers about printed values.
    /// </remarks>
    public Func<GameState, IAbilitySource, GameObject, bool>? StateCondition { get; init; }

    /// <summary>
    /// Where the source has to be for the ability to work. Almost everything triggers from the
    /// battlefield (CR 603.6); "when this dies" and "when you discard this" do not.
    /// </summary>
    public Zone FunctionsFrom { get; init; } = Zone.Battlefield;

    /// <summary>
    /// Whether this ability still works while the permanent is face down (CR 707.2).
    /// </summary>
    /// <remarks>
    /// False for everything the card prints, because a face-down permanent has none of its card's
    /// abilities. Disguise's ward is the exception (CR 702.168a): it belongs to the keyword rather
    /// than to the card, so it is the one thing a face-down permanent has besides being a 2/2.
    /// </remarks>
    public bool FunctionsFaceDown { get; init; }

    /// <summary>What it does when it resolves (CR 608.2c).</summary>
    public System.Collections.Immutable.ImmutableList<IEffect> Effects { get; init; } = [];

    /// <summary>What it targets, chosen as it goes on the stack (CR 603.3d).</summary>
    public System.Collections.Immutable.ImmutableList<TargetSpec> Targets { get; init; } = [];

    /// <summary>
    /// The modes offered, when the ability says "choose one —" (CR 700.2).
    /// </summary>
    /// <remarks>
    /// The same shape a spell's modes have, and for the same reason: a mode is an ordinary
    /// sentence and is compiled by the same phrase parser. What differs is *when* the choice
    /// happens - a spell's modes are chosen as it is cast (CR 601.2b) and an ability's as it is
    /// put on the stack (CR 603.3c). Two moments in the engine, one question.
    /// </remarks>
    public System.Collections.Immutable.ImmutableList<SpellMode> Modes { get; init; } = [];

    /// <summary>How many modes the controller picks, or zero when the ability has none.</summary>
    public int ModesToChoose { get; init; }

    /// <summary>
    /// The most that may be picked, which is <see cref="ModesToChoose"/> unless the card says
    /// "one or both" or "one or more".
    /// </summary>
    public int ModesMax { get; init; }

    /// <summary>
    /// Whether this may trigger only once each turn (CR 603.1).
    /// </summary>
    /// <remarks>
    /// Printed as a sentence after the ability rather than as part of the condition, so it is
    /// lifted off the text the same way an activation limit is. Without it a card that says
    /// "only once each turn" triggers every time, which on a draw or damage trigger is the
    /// difference between a fair card and an engine.
    /// </remarks>
    public bool OncePerTurn { get; init; }
}

/// <summary>
/// Where the engine finds out what abilities a card has.
/// </summary>
/// <remarks>
/// The seam for the card layer. Nothing implements this yet beyond tests: the backbone is being
/// settled before card behaviour exists, and this is the shape the card definitions of slice 8
/// will plug into.
/// </remarks>
public interface IAbilitySource : ISpellSource, ICostModifierSource
{
    /// <summary>The triggered abilities of a card, or an empty list if it has none.</summary>
    IReadOnlyList<TriggeredAbilityDefinition> TriggersOf(CardDefinition card);

    /// <summary>What this card does when cast as an Adventure, if it has one (CR 715.2).</summary>
    SpellDefinition? AdventureOf(CardDefinition card) => null;

    /// <summary>What the Adventure half costs, exactly as printed (CR 715.3a).</summary>
    string? AdventureCostOf(CardDefinition card) => null;

    /// <summary>The cleaved reading of a cleave card, if it has one (CR 702.148a).</summary>
    SpellDefinition? CleaveSpellOf(CardDefinition card) => null;

    /// <summary>What the cleaved cast costs, exactly as printed (CR 702.148a).</summary>
    string? CleaveCostOf(CardDefinition card) => null;

    /// <summary>The promised reading of an instant or sorcery with gift (CR 702.174).</summary>
    SpellDefinition? GiftSpellOf(CardDefinition card) => null;

    /// <summary>Whether this card offers a gift as it is cast (CR 702.174a).</summary>
    bool HasGift(CardDefinition card) => false;

    /// <summary>The spell a prepared permanent offers a copy of, if it has one.</summary>
    SpellDefinition? PreparedSpellOf(CardDefinition card) => null;

    /// <summary>What a prepared permanent's spell costs, exactly as printed.</summary>
    string? PreparedCostOf(CardDefinition card) => null;

    /// <summary>Whether a prepared permanent's spell may be cast at instant speed.</summary>
    bool PreparedIsInstantOf(CardDefinition card) => false;

    /// <summary>How much each creature devoured is worth in counters (CR 702.81a).</summary>
    int DevourCountOf(CardDefinition card) => 0;

    /// <summary>How much each card revealed to amplify is worth in counters (CR 702.38a).</summary>
    int AmplifyCountOf(CardDefinition card) => 0;

    /// <summary>Whether this Saga starts at a chapter its controller picks (CR 702.155b).</summary>
    bool HasReadAhead(CardDefinition card) => false;

    /// <summary>What this card may be cast for when drawn as a miracle (CR 702.94a).</summary>
    Mana.ManaCostSpec? MiracleCostOf(CardDefinition card) => null;

    /// <summary>The separately castable halves of a split card (CR 709.4).</summary>
    IReadOnlyList<CardHalf> HalvesOf(CardDefinition card) => [];

    /// <summary>Whether both halves may be cast together as one spell (CR 702.102a).</summary>
    bool HasFuse(CardDefinition card) => false;

    /// <summary>
    /// The continuous effects a card's static abilities produce (CR 604.2).
    /// </summary>
    /// <remarks>
    /// Asked of every permanent on the battlefield each time characteristics are computed, which
    /// is what makes a lord's bonus vanish the moment the lord does.
    /// </remarks>
    IReadOnlyList<ContinuousEffectDefinition> StaticsOf(CardDefinition card) => [];

    /// <summary>
    /// The continuous effects a card's static abilities apply to <em>players</em> (CR 702.11c).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="StaticsOf"/> because the subject is not an object and cannot be
    /// described by one: an effect here is asked about a seat at the table, not about a
    /// permanent. Asked of every permanent on the battlefield each time a player's abilities are
    /// computed, for the same reason its sibling is — a hexproof a player keeps after the
    /// enchantment granting it has gone is the stored-characteristic bug in a new place.
    /// </remarks>
    IReadOnlyList<PlayerQualityDefinition> PlayerQualitiesOf(CardDefinition card) => [];

    /// <summary>
    /// Keywords the card's own rules text gives it beyond those printed as keywords (CR 702).
    /// </summary>
    /// <remarks>
    /// "This creature can't block" is a keyword ability in everything but name: it is one clause
    /// in the blocking rules, and the engine asks for it by flag. Recording it here rather than
    /// as an effect keeps that question in one place.
    /// </remarks>
    KeywordAbility GrantedKeywords(CardDefinition card) => KeywordAbility.None;

    /// <summary>
    /// What this permanent takes off the cost of its controller's spells (CR 601.2f).
    /// </summary>
    /// <remarks>
    /// The one cell of the modifier grid a hand-written script can say in two words, and the
    /// only reason it survives <see cref="CostModifier"/>: nothing compiled emits one any more.
    /// The compiler reads every cell it can read into <see cref="ICostModifierSource"/>, which
    /// this interface now extends, and <c>Game</c> translates a reducer into the modifier it is
    /// exactly equal to rather than applying it down a second path. Emitting both for one card
    /// would discount it twice.
    /// </remarks>
    IReadOnlyList<CostReducer> CostReducersOf(CardDefinition card) => [];

    /// <summary>
    /// Whether this permanent lets its controller see the top of their library (CR 401.2).
    /// </summary>
    bool ShowsTopOfLibrary(CardDefinition card) => false;

    /// <summary>Whether this permanent removes its controller's hand limit (CR 402.2).</summary>
    bool RemovesHandLimit(CardDefinition card) => false;

    /// <summary>
    /// How much this permanent takes off somebody's maximum hand size (CR 402.2).
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="RemovesHandLimit"/>, and a list rather than a number because
    /// the two directions are different questions: "your maximum hand size is reduced by three"
    /// moves the controller's limit and "each opponent's maximum hand size is reduced by two"
    /// moves everybody else's, and one card could print both.
    /// </remarks>
    IReadOnlyList<HandLimitReduction> HandLimitReductionsOf(CardDefinition card) => [];

    /// <summary>What this permanent chooses as it enters, if anything (CR 614.12).</summary>
    ChoiceOnEntry ChoosesOnEntry(CardDefinition card) => ChoiceOnEntry.None;

    /// <summary>How many extra lands its controller may play each turn (CR 305.2).</summary>
    int ExtraLandDrops(CardDefinition card) => 0;

    /// <summary>Whether its controller may leave it tapped at untap (CR 502.3).</summary>
    bool MayDeclineUntap(CardDefinition card) => false;

    /// <summary>Whether its controller skips their draw step (CR 504.1).</summary>
    bool SkipsDrawStep(CardDefinition card) => false;

    /// <summary>
    /// How many spells this permanent lets a player cast in a turn (CR 601.3).
    /// </summary>
    /// <remarks>
    /// A list rather than a single value because nothing stops a card printing two, and because
    /// the sweep that reads it already visits every permanent once — an answer that could only be
    /// one limit would need a second question the day a card had two.
    /// </remarks>
    IReadOnlyList<CastLimit> CastLimitsOf(CardDefinition card) => [];

    /// <summary>
    /// Whether this permanent turns its controller's top card face up for everyone (CR 401.2).
    /// </summary>
    bool RevealsTopOfLibrary(CardDefinition card) => false;

    /// <summary>
    /// Whether its owner may start the game with this card on the battlefield (CR 103.6a).
    /// </summary>
    /// <remarks>
    /// Asked of a card in an opening hand, once, in the step between the last mulligan and the
    /// first turn. Every other question on this interface is about a permanent; this one has no
    /// permanent to be about yet, which is exactly why it is here rather than being an ability.
    /// </remarks>
    bool MayBeginOnBattlefield(CardDefinition card) => false;

    /// <summary>
    /// The land type a creature's attack is conditional on, or null (CR 506.3).
    /// </summary>
    /// <remarks>
    /// "This creature can't attack unless defending player controls an Island" is a restriction
    /// on the declaration rather than anything the creature has, so it cannot be a keyword: the
    /// question is about the defender's board and is not answerable until an attack names one.
    /// </remarks>
    string? AttacksOnlyIfDefenderControls(CardDefinition card) => null;

    /// <summary>The replacement effects a card produces (CR 614).</summary>
    IReadOnlyList<ReplacementEffectDefinition> ReplacementsOf(CardDefinition card) => [];

    /// <summary>
    /// Looks up an effect created by a resolved spell or ability, by the id the game recorded.
    /// </summary>
    /// <remarks>
    /// These outlive their source (CR 613.7b) — "target creature gets +3/+3 until end of turn"
    /// keeps working after the spell is in the graveyard — so the game records that the effect
    /// exists and finds out what it does again from here.
    /// </remarks>
    ContinuousEffectDefinition? FloatingEffect(string definitionId) => null;
}

/// <summary>A card pool with no abilities at all — the default while cards do nothing.</summary>
public sealed class NoAbilities : IAbilitySource
{
    public static readonly NoAbilities Instance = new();

    public IReadOnlyList<TriggeredAbilityDefinition> TriggersOf(CardDefinition card) => [];
}
