using System.Collections.Immutable;

namespace MtgEngine.Rules.State;

/// <summary>What kind of decision the game is waiting on.</summary>
/// <remarks>
/// Each value names a place the rules say a player chooses. The engine used to answer these
/// itself — keeping the oldest legend, taking replacement effects in timestamp order, putting a
/// player's triggers on the stack in the order they happened — and each of those was a comment
/// saying "until there is a way to ask them". This is the way to ask them.
/// </remarks>
public enum ChoiceKind
{
    /// <summary>Whether to take a mulligan (CR 103.5).</summary>
    Mulligan,

    /// <summary>Which cards to put on the bottom after a mulligan (CR 103.5).</summary>
    BottomAfterMulligan,

    /// <summary>
    /// Which cards in your opening hand start the game on the battlefield (CR 103.6a).
    /// </summary>
    /// <remarks>
    /// The one question asked between the last mulligan and the first turn, and the only one in
    /// the game whose options are cards a player is holding rather than things on a board.
    /// Picking nothing is declining — "you may" — so it has no minimum, and the maximum is every
    /// eligible card, because CR 103.6 lets the player take any such action "in any order" and a
    /// hand can hold two Leylines.
    /// </remarks>
    OpeningHandBattlefield,

    /// <summary>Which duplicate legendary permanent to keep (CR 704.5j).</summary>
    LegendRule,

    /// <summary>The order to put your simultaneous triggers on the stack (CR 603.3b).</summary>
    OrderTriggers,

    /// <summary>Which applicable replacement effect to apply next (CR 616.1).</summary>
    OrderReplacements,

    /// <summary>How to divide an attacker's damage among its blockers (CR 510.1c).</summary>
    DivideCombatDamage,

    /// <summary>Which cards to discard down to maximum hand size (CR 514.1).</summary>
    DiscardToHandSize,

    /// <summary>
    /// Which cards to discard because an effect said so (CR 701.9a).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="DiscardToHandSize"/> even though the answer looks the same,
    /// because they resume differently: cleanup's discard ends the step, while this one hands
    /// priority back to whoever was about to get it mid-turn.
    /// </remarks>
    DiscardToEffect,

    /// <summary>
    /// Whether to pay an optional cost an effect offered (CR 601.2b).
    /// </summary>
    /// <remarks>
    /// The only choice whose answer decides what the rest of an effect <em>is</em>. It is
    /// answerable from a log because the choice carries a locator back into the card's own
    /// definition rather than a captured branch — see <c>MayPay</c>.
    /// </remarks>
    OptionalPayment,

    /// <summary>
    /// Which of the cards you just looked at to keep (CR 701.20a).
    /// </summary>
    /// <remarks>
    /// Unlike a scry, the answer names what to <em>take</em> rather than what to put back — which
    /// is what the cards say, and which matters when there is nothing worth taking: picking none
    /// is legal and puts them all on the bottom.
    /// </remarks>
    LookAndTake,

    /// <summary>Which card a search of your library turns up (CR 701.23).</summary>
    /// <remarks>
    /// Finding nothing is always legal — a player may fail to find even when the card is there
    /// (CR 701.23c) — so this never has a minimum.
    /// </remarks>
    SearchLibrary,

    /// <summary>
    /// Which permanents and players to proliferate (CR 701.34a).
    /// </summary>
    /// <remarks>
    /// "Any number" — including none — so it has no minimum, and unusually it may name players as
    /// well as permanents, because poison and energy live on players.
    /// </remarks>
    Proliferate,

    /// <summary>
    /// Which card to take from another player's hand (CR 701.16).
    /// </summary>
    /// <remarks>
    /// The one choice whose options are hidden information belonging to somebody else. It is only
    /// ever offered after the hand has been revealed, which is what makes showing them legal.
    /// </remarks>
    /// <summary>
    /// A colour or creature type named as a permanent enters (CR 614.12).
    /// </summary>
    NameCharacteristic,

    /// <summary>
    /// Which of the permanents that may decline to untap actually do (CR 502.3).
    /// </summary>
    ChooseOptionalUntaps,

    ChooseCardInHand,

    /// <summary>
    /// Which permanents or cards to give up so a permanent is not sacrificed.
    /// </summary>
    /// <remarks>
    /// Picking nothing is declining, which is why this is one question rather than a yes/no
    /// followed by a selection.
    /// </remarks>
    PayOrSacrifice,

    /// <summary>Which end of their library a permanent's owner puts it on.</summary>
    LibraryEnd,

    /// <summary>Which creatures are eaten as a devouring creature enters (CR 702.81a).</summary>
    Devour,

    /// <summary>
    /// Which cards are revealed from hand as an amplifying creature enters (CR 702.38a).
    /// </summary>
    /// <remarks>
    /// Devour's question asked of the hand rather than the battlefield, and it keeps devour's
    /// shape for the same reason: revealing none is declining, so it is one question rather than
    /// a yes/no followed by a selection.
    /// </remarks>
    Amplify,

    /// <summary>
    /// Which chapter a Saga with read ahead starts at (CR 702.155b).
    /// </summary>
    /// <remarks>
    /// A number between one and the Saga's final chapter, asked as it enters. The options are
    /// the numbers themselves rather than a free entry, because every choice in this engine is
    /// a pick from a list the game offers - which is what makes an answer checkable when it
    /// arrives from outside.
    /// </remarks>
    ReadAhead,

    /// <summary>
    /// Whether to reveal the top cards for a ripple (CR 702.60a).
    /// </summary>
    /// <remarks>
    /// One option, and picking nothing declines - the same shape devour uses, because "you may"
    /// followed by a thing to do is one question rather than a yes/no and then a selection. It
    /// is a real decision rather than a formality: rippling shuffles what was on top of the
    /// library into the bottom in a random order, which a player who has just scried does not
    /// want.
    /// </remarks>
    Ripple,

    /// <summary>Which creature a ciphered spell is encoded on, if any (CR 702.99a).</summary>
    EncodeOnCreature,

    /// <summary>
    /// Whether an attacker assigns its combat damage as though it weren't blocked (CR 510.1a).
    /// </summary>
    AssignAsThoughUnblocked,

    /// <summary>Which card to discard for a connive, deciding the counter (CR 701.50a).</summary>
    Connive,

    /// <summary>Which of the two cards looked at to manifest (CR 701.62a).</summary>
    ManifestDread,

    /// <summary>Which creature token of yours to copy (CR 701.36a).</summary>
    Populate,

    /// <summary>Which creature to sacrifice to exploit, or none (CR 702.110a).</summary>
    Exploit,

    /// <summary>Which unpaired creature a soulbond creature pairs with, or none (CR 702.95a).</summary>
    /// <remarks>
    /// Exploit's shape: "you may pair" is answered by naming a creature or declining, so it is
    /// one question rather than a yes/no followed by a selection. The "whenever another creature
    /// you control enters" arm arrives here with a single candidate — the newcomer — and the
    /// same question serves as its yes or no.
    /// </remarks>
    Soulbond,

    /// <summary>Naming one of the five colours, for an effect that asks (CR 202.2).</summary>
    ChooseColor,

    /// <summary>Which creature type a permanent becomes (CR 205.1b).</summary>
    ChooseCreatureType,

    /// <summary>Whether to put a clashed card on the bottom of your library (CR 701.30a).</summary>
    ClashKeepOnTop,

    /// <summary>Which creature you control bears the Ring (CR 701.54a).</summary>
    RingBearer,

    /// <summary>
    /// Which player protects a battle (CR 310.9a).
    /// </summary>
    /// <remarks>
    /// Asked of the battle's controller as it enters, and again when its protector stops being
    /// eligible (CR 704.5x, 704.5y). Only asked when the answer could differ: a Siege at a
    /// two-player table has exactly one opponent (CR 310.12a), and a forced choice is made
    /// rather than offered.
    /// </remarks>
    ChooseProtector,

    /// <summary>Which arrow to follow out of a dungeon room (CR 701.49b).</summary>
    VentureRoom,

    /// <summary>Choosing which permanents an effect untaps, up to a limit (CR 701.21a).</summary>
    ChooseUntaps,

    /// <summary>Choosing which permanent takes counters, for bolster and amass (CR 701.36a).</summary>
    ChooseForCounters,

    /// <summary>Arranging cards you have looked at back on top of your library (CR 701.19a).</summary>
    OrderLibraryTop,

    /// <summary>Which of your permanents an effect names but does not target (CR 609.4).</summary>
    /// <remarks>
    /// Distinct from choosing a target: this happens on resolution rather than on casting, so
    /// hexproof does not apply and the spell does not fizzle if nothing qualifies.
    /// </remarks>
    ChoosePermanent,

    /// <summary>What a triggered ability targets, as it goes on the stack (CR 603.3d).</summary>
    ChooseTriggerTargets,

    /// <summary>
    /// Which mode a triggered ability takes, as it goes on the stack (CR 603.3c).
    /// </summary>
    /// <remarks>
    /// A spell's modes are chosen while it is being cast and never reach a pending choice; an
    /// ability's are chosen at a moment the engine is already mid-emission, which is why this one
    /// has to be asked and resumed the way the trigger's targets are.
    /// </remarks>
    ChooseTriggerMode,

    /// <summary>Which creature to tap to enlist, as this one attacks (CR 702.154a).</summary>
    /// <remarks>
    /// "Up to one", so declining is always on the menu - and the question is asked as the attack
    /// is declared rather than on resolution, because the tap is part of attacking (CR 508.1g).
    /// </remarks>
    Enlist,

    /// <summary>Which of the cards you looked at go to the bottom (CR 701.22, scry).</summary>
    Scry,

    /// <summary>Which of the cards you looked at go to the graveyard (CR 701.25, surveil).</summary>
    Surveil,
}

/// <summary>One thing a player may pick.</summary>
public sealed record ChoiceOption(string Id, string Label)
{
    /// <summary>
    /// For a division choice, how much is being assigned to this option (CR 510.1c).
    /// </summary>
    /// <remarks>
    /// Carried on the option rather than in a parallel list so an answer cannot pair the wrong
    /// number with the wrong target.
    /// </remarks>
    public int Amount { get; init; }
}

/// <summary>
/// A decision the game is waiting on, and cannot proceed without.
/// </summary>
/// <remarks>
/// The game stops here. Nothing else may happen while a choice is outstanding — not another
/// player's action, not a state-based action, not a step ending — because everything downstream
/// depends on the answer. That is exactly how the rules work: the game does not carry on around
/// a player who has not decided.
/// <para>
/// The engine's alternative was to answer for them, which it did in three places and which is
/// wrong in a way nobody would ever see: keeping the older legend is a legal choice, so a player
/// robbed of the decision gets a legal game that is not the one they would have played.
/// </para>
/// </remarks>
public sealed record PendingChoice
{
    public required string Id { get; init; }

    /// <summary>Who has to answer. Only they may.</summary>
    public required Guid PlayerId { get; init; }

    public required ChoiceKind Kind { get; init; }

    /// <summary>What to show the player.</summary>
    public required string Prompt { get; init; }

    public ImmutableList<ChoiceOption> Options { get; init; } = [];

    /// <summary>Fewest options that may be picked.</summary>
    public int MinPicks { get; init; } = 1;

    /// <summary>Most options that may be picked. Equal to the option count for an ordering.</summary>
    public int MaxPicks { get; init; } = 1;

    /// <summary>
    /// True when the answer is a sequence rather than a set — the order of the picks is the
    /// answer (CR 603.3b, 616.1).
    /// </summary>
    public bool IsOrdering =>
        Kind is ChoiceKind.OrderTriggers or ChoiceKind.OrderReplacements
            or ChoiceKind.OrderLibraryTop;

    /// <summary>
    /// True when the answer is an amount per option rather than a selection (CR 510.1c).
    /// </summary>
    /// <remarks>
    /// Dividing damage is not a choice of which blockers, nor an order: it is "how much to
    /// each". Two damage to each of two three-toughness blockers, killing neither, is a legal
    /// division that no ordering can express.
    /// </remarks>
    public bool IsDivision => Kind is ChoiceKind.DivideCombatDamage;

    /// <summary>How much is being divided, for a division choice (CR 510.1a).</summary>
    public int TotalToDivide { get; init; }

    /// <summary>
    /// Who receives priority once this is answered (CR 117.5).
    /// </summary>
    /// <remarks>
    /// "The player who would have received priority does so" — which is not always the active
    /// player. A question raised while priority was passing to an opponent has to hand it back
    /// to that opponent; granting it to the active player instead silently skips the window the
    /// opponent was about to get, and does it in a way nobody would ever see.
    /// <para>
    /// On the choice rather than in a field on the game, because a log has to replay a game that
    /// is mid-question and then answer it correctly.
    /// </para>
    /// </remarks>
    public Guid? ResumePriorityTo { get; init; }

    /// <summary>Extra state the resumption needs, opaque to everyone else.</summary>
    /// <remarks>
    /// Only ever ids the engine put there itself — the attacker whose damage is being divided,
    /// the permanent that triggered the legend rule. It is in state rather than in a field on
    /// <see cref="Engine.Game"/> so a replayed log rebuilds a game that is mid-question exactly
    /// as it was.
    /// </remarks>
    public ImmutableList<string> Context { get; init; } = [];

    public bool Equals(PendingChoice? other) =>
        other is not null &&
        string.Equals(Id, other.Id, StringComparison.Ordinal) &&
        PlayerId == other.PlayerId &&
        Kind == other.Kind &&
        MinPicks == other.MinPicks &&
        MaxPicks == other.MaxPicks &&
        ResumePriorityTo == other.ResumePriorityTo &&
        TotalToDivide == other.TotalToDivide &&
        Structural.Same(Options, other.Options) &&
        Structural.Same(Context, other.Context);

    public override int GetHashCode() =>
        HashCode.Combine(Id, PlayerId, Kind, Options.Count, MinPicks, MaxPicks);
}
