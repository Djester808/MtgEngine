using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Events;

/// <summary>
/// Something that happened. The event log is the game: <see cref="GameState"/> is a fold of it.
/// </summary>
/// <remarks>
/// Events describe what <em>did</em> happen, never what was asked for — a rejected action emits
/// nothing. That is what makes the log replayable, and replay is the property this engine was
/// rebuilt to have: a reported game can be re-run exactly, and a failing one can be pasted into
/// a test as-is. Anything non-deterministic (a shuffle, a die roll) records its outcome here
/// rather than the seed that produced it, so a replay cannot drift from the game it describes.
/// </remarks>
public abstract record GameEvent
{
    /// <summary>One line for a human reading the log.</summary>
    public abstract string Describe();

    /// <summary>
    /// The Comprehensive Rules paragraph this event answers to, where one does — "704.5b" for a
    /// player losing to an empty library. The rules text is a live asset in this repo, so a log
    /// line can be traced to the sentence that caused it instead of to a comment about it.
    /// </summary>
    public virtual string? Rule => null;
}

/// <summary>Why an object is changing zones (CR 400.6: "determine what event is moving the object").</summary>
/// <remarks>
/// Carried on the move rather than split into an event type per verb, because triggered
/// abilities ask about the cause ("whenever a creature dies", "whenever you draw a card") while
/// the state change is the same move in every case.
/// </remarks>
public enum MoveCause
{
    Other,
    Draw,

    /// <summary>
    /// Destroyed by something that says it can't be regenerated (CR 701.19c).
    /// </summary>
    /// <remarks>
    /// A separate cause rather than a flag on the move, because the only thing in the game that
    /// asks is the regeneration replacement, and it asks by cause. Everything else that watches a
    /// permanent die watches the zone change and cannot tell the two apart — which is right, since
    /// a creature destroyed this way has still died in every sense a card can ask about.
    /// </remarks>
    DestroyNoRegeneration,
    Discard,
    Play,
    Cast,
    Resolve,
    Destroy,
    Sacrifice,
    Mill,
    Exile,
    Return,
    StateBasedAction,

    /// <summary>
    /// An Attraction moved from its owner's Attraction deck onto the battlefield (CR 701.51b).
    /// </summary>
    /// <remarks>
    /// A cause rather than an event of its own, for the reason
    /// <see cref="DestroyNoRegeneration"/> is one: the move already says everything that
    /// happened, and "whenever you open an Attraction" (CR 701.51c) is the only thing in the game
    /// that has to tell this move from any other card leaving the command zone. A commander
    /// returning to the battlefield from the command zone is not an opening, and without the
    /// cause nothing could see the difference.
    /// </remarks>
    OpenAttraction,
}

/// <summary>Which end of an ordered zone an object arrives at (CR 400.5).</summary>
public enum ZonePosition
{
    Top,
    Bottom,
}

/// <summary>One player's seat as the game begins (CR 103).</summary>
public sealed record Seat(
    Guid PlayerId,
    string Name,
    int StartingLife,
    ImmutableList<DealtCard> Deck);

/// <summary>A card and the identity it starts the game with, before any shuffle.</summary>
public sealed record DealtCard(ObjectId Id, CardDefinition Card);

/// <summary>
/// The game exists: seats are taken, decks have become libraries (CR 401.1).
/// </summary>
/// <remarks>
/// Fat on purpose. It is the genesis event, and a log that begins here needs no other input to
/// reconstruct the game — including which cards were in which deck.
/// </remarks>
public sealed record GameStarted(
    Guid GameId,
    ImmutableList<Seat> Seats,
    Guid StartingPlayerId) : GameEvent
{
    public override string Rule => "103";

    public override string Describe() =>
        $"Game {GameId:N} started with {Seats.Count} players; {Seats.First(s => s.PlayerId == StartingPlayerId).Name} goes first.";
}

/// <summary>
/// A library was shuffled, and this is the order it came out in (CR 103.2, 701.24).
/// </summary>
/// <remarks>
/// The resulting order is recorded, not the seed. A seed only reproduces the shuffle if the
/// shuffling algorithm never changes; the order reproduces it forever.
/// </remarks>
public sealed record LibraryShuffled(Guid PlayerId, ImmutableList<ObjectId> Order) : GameEvent
{
    public override string Rule => "701.24";

    public override string Describe() => $"{PlayerId:N} shuffled ({Order.Count} cards).";
}

/// <summary>
/// An object moved from one zone to another and became a new object (CR 400.7).
/// </summary>
/// <remarks>
/// <see cref="NewId"/> is not decoration. Anything holding the old id is holding a reference to
/// something that no longer exists, which is the rule working as intended: an aura attached to
/// a creature that died must not find it again when it returns.
/// </remarks>
/// <param name="ControllerId">Who controls it on arrival, for a zone that has controllers.</param>
/// <param name="LeavingControllerId">
/// Who controlled it as it left the battlefield, if it was on one (CR 613.1b).
/// </param>
/// <remarks>
/// The leaving controller is a different question from <paramref name="ControllerId"/> and cannot
/// be answered by it: that one says who controls the object where it is going, and for a hand, a
/// library or a graveyard the answer is simply its owner (CR 400.3). "If a permanent left the
/// battlefield under your control this turn" — revolt, and twenty-six corpus lines — asks the
/// other end of the move.
/// <para>
/// It is on the event rather than worked out by the fold because <b>control is layer 2</b>
/// (CR 613.1b): the controller stored on an object is only where its control <em>started</em>, so
/// a stolen permanent that dies would be filed against the player it was taken from. The reducer
/// has no ability source and cannot compute the current controller; the engine can, and stamps it
/// here, on the one path every event takes. Null means nobody stamped it — a hand-built event in
/// a test, or a log written before the field existed — and the fold falls back to the stored
/// controller rather than refusing to fold.
/// </para>
/// </remarks>
public sealed record ObjectMoved(
    ObjectId OldId,
    ObjectId NewId,
    Zone From,
    Zone To,
    Guid ControllerId,
    MoveCause Cause,
    ZonePosition Position = ZonePosition.Top,
    Guid? LeavingControllerId = null) : GameEvent
{
    public override string Rule => "400.7";

    public override string Describe() => $"{OldId} moved {From} -> {To} ({Cause}), now {NewId}.";
}

/// <summary>A player's life total changed (CR 119.3).</summary>
public sealed record LifeChanged(Guid PlayerId, int Delta, int NewTotal) : GameEvent
{
    public override string Rule => "119.3";

    public override string Describe() =>
        $"{PlayerId:N} {(Delta >= 0 ? "gained" : "lost")} {Math.Abs(Delta)} life ({NewTotal}).";
}

/// <summary>
/// A player was asked to draw from an empty library (CR 121.4).
/// </summary>
/// <remarks>
/// The draw simply does not happen; the player does not lose here. They lose the next time
/// state-based actions are checked (CR 704.5b), which is a different moment and can be
/// undone in between by an effect that replaces the loss.
/// </remarks>
public sealed record DrawFromEmptyLibraryAttempted(Guid PlayerId) : GameEvent
{
    public override string Rule => "121.4";

    public override string Describe() => $"{PlayerId:N} tried to draw from an empty library.";
}

// ---- Turn structure and priority (slice 2) ----------------------------------------------

/// <summary>A new turn began (CR 500.1). Resets what is once-per-turn.</summary>
public sealed record TurnBegan(int TurnNumber, Guid ActivePlayerId) : GameEvent
{
    public override string Rule => "500.1";

    public override string Describe() => $"Turn {TurnNumber} began ({ActivePlayerId:N} active).";
}

/// <summary>A step began (CR 500.1). The previous one is over by definition (CR 500.12).</summary>
public sealed record StepBegan(TurnStep Step) : GameEvent
{
    public override string Rule => "500.1";

    public override string Describe() => $"{Step} began.";
}

/// <summary>
/// A player received priority, and the run of passes was broken (CR 117.3a-c).
/// </summary>
/// <remarks>
/// Emitted at the start of a step, after a resolution, and after any action — all the cases
/// where CR 117.4's "in succession" starts over.
/// </remarks>
public sealed record PriorityGranted(Guid PlayerId) : GameEvent
{
    public override string Rule => "117.3";

    public override string Describe() => $"{PlayerId:N} has priority.";
}

/// <summary>A player passed; the next player in turn order receives priority (CR 117.3d).</summary>
public sealed record PriorityPassed(Guid PlayerId, Guid NextPlayerId) : GameEvent
{
    public override string Rule => "117.3d";

    public override string Describe() => $"{PlayerId:N} passed to {NextPlayerId:N}.";
}

/// <summary>Nobody has priority: the untap step, cleanup, or a resolution in progress.</summary>
public sealed record PriorityWithdrawn : GameEvent
{
    public override string Rule => "117.2e";

    public override string Describe() => "No player has priority.";
}

/// <summary>The active player's permanents untapped (CR 502.3). One event, one simultaneous act.</summary>
public sealed record PermanentsUntapped(ImmutableList<ObjectId> Ids) : GameEvent
{
    public override string Rule => "502.3";

    public override string Describe() => $"{Ids.Count} permanent(s) untapped.";
}

/// <summary>A permanent became tapped (CR 701.26a).</summary>
/// <summary>
/// One permanent became attached to another, or to nothing (CR 701.3).
/// </summary>
/// <remarks>
/// <paramref name="To"/> is null for unattaching, which is a real event rather than an absence:
/// an Equipment whose creature died is still on the battlefield attached to nothing, and the log
/// has to say when it came loose so a replay reaches the same board.
/// </remarks>
public sealed record PermanentAttached(ObjectId Id, ObjectId? To, Guid? ToPlayer = null)
    : GameEvent
{
    public override string Rule => "701.3";

    public override string Describe() =>
        To is null ? $"{Id} was unattached." : $"{Id} was attached to {To}.";
}

/// <summary>A regeneration shield was created, or spent (CR 701.19).</summary>
public sealed record RegenerationShieldsChanged(ObjectId Id, int Delta) : GameEvent
{
    public override string Rule => "701.19";

    public override string Describe() =>
        Delta > 0 ? $"{Id} was regenerated." : $"{Id} used a regeneration shield.";
}

/// <summary>
/// A player is to look at the top cards of their library and sort them (CR 701.22, 701.25).
/// </summary>
/// <remarks>
/// The looking is not the interesting part — the choice is, and a choice halts the game. So the
/// effect records that the question is owed and the engine asks it at the next settle, rather
/// than trying to stop half way through a resolution. Scry and surveil differ only in where the
/// unwanted cards go, which is why they are one event with a flag.
/// </remarks>
public sealed record LookAtTopRequested(Guid PlayerId, int Count, bool ToGraveyard) : GameEvent
{
    public override string Rule => ToGraveyard ? "701.25" : "701.22";

    public override string Describe() =>
        $"{PlayerId:N} looks at the top {Count} card(s) of their library.";
}

/// <summary>
/// A player owes a discard, asked at the next settle (CR 701.9).
/// </summary>
/// <remarks>
/// The same shape as <see cref="LookAtTopRequested"/>, for the same reason: which card to discard
/// is the player's choice (CR 701.9a) and a choice halts the game, so the request is recorded
/// where the effect resolves and the question is asked just after it.
/// </remarks>
public sealed record DiscardRequested(Guid PlayerId, int Count, bool AtRandom) : GameEvent
{
    public override string Rule => "701.9";

    public override string Describe() =>
        $"{PlayerId:N} discards {Count} card(s){(AtRandom ? " at random" : string.Empty)}.";
}

/// <summary>
/// A player is being offered an optional payment, asked at the next settle (CR 601.2b).
/// </summary>
/// <remarks>
/// "You may pay {2}. If you do, draw a card." The answer decides what the rest of the effect is,
/// which is the one thing the deferred-question pattern cannot normally do — so instead of
/// carrying a continuation this carries a <em>locator</em>: which permanent, which of its
/// abilities, and which effect within it. The branch is looked up again from the card when the
/// answer arrives, so nothing here is anything a log cannot rebuild.
/// </remarks>
public sealed record OptionalPaymentRequested(
    Guid PlayerId,
    ObjectId SourceId,
    string? AbilityId,
    int EffectIndex,
    string CostText) : GameEvent
{
    /// <summary>
    /// What to call the two answers, when the offer is a choice rather than a price (CR 601.2b).
    /// </summary>
    /// <remarks>
    /// A free "you may" is the engine's binary choice on resolution, and fabricate is one:
    /// counters or tokens, neither of them a payment. Without words for the two sides the player
    /// is asked "Pay ?" and has to know the card by heart to answer.
    /// </remarks>
    public string? YesLabel { get; init; }

    /// <inheritdoc cref="YesLabel"/>
    public string? NoLabel { get; init; }

    /// <summary>
    /// What the ability that made the offer was aimed at (CR 601.2c, 603.3d).
    /// </summary>
    /// <remarks>
    /// Carried rather than looked up. The offer is answered after the thing that made it has
    /// resolved, and for a death trigger the source is not merely off the stack — it is a
    /// permanent that has left the battlefield, so its id names nothing at all (CR 400.7).
    /// Reading the targets off the live source found none, and every "when this dies, you may
    /// return target card ..." did nothing after the player had said yes.
    /// </remarks>
    public ImmutableList<Target> Targets { get; init; } = [];

    /// <summary>
    /// The object the trigger that made the offer was about (CR 603.2).
    /// </summary>
    /// <remarks>
    /// Carried for the same reason as the targets, and found the same way it was lost: a branch
    /// runs against the <em>physical</em> source — the permanent whose ability this was — and a
    /// permanent has no ability on it and so no subject. Ward's "counter it" ran with nothing to
    /// counter and did nothing, in silence, on a board where the spell was still sitting on the
    /// stack waiting to resolve.
    /// </remarks>
    public ObjectId? SubjectObject { get; init; }

    /// <summary>
    /// The player the trigger that made the offer was about (CR 603.2).
    /// </summary>
    /// <remarks>
    /// The subject object's twin, and missing for exactly the reason it was: a branch runs
    /// against the permanent, which carries no ability and so remembers no subject. Nothing
    /// needed it until there was a punisher to notice - the counterspell tax reaches its player
    /// through a target. "That player loses 5 life unless they discard a card" reaches theirs
    /// through the trigger, so declining ran a branch that named nobody and took no life, in
    /// silence, on a card the coverage number counted as read.
    /// </remarks>
    public Guid? SubjectPlayer { get; init; }

    /// <summary>
    /// How much the event was about, for a trigger that says "that many" (CR 603.2).
    /// </summary>
    /// <remarks>
    /// Damage dealt, life gained, counters put on — the number the triggering event carried. It
    /// travels with the trigger rather than being looked up again on resolution, because by then
    /// the event is over and nothing in the state remembers how much it was.
    /// </remarks>
    public int? SubjectAmount { get; init; }

    /// <summary>
    /// How many times over the price is owed (CR 702.24a).
    /// </summary>
    /// <remarks>
    /// Cumulative upkeep charges its cost once per age counter. Mana says that in
    /// <see cref="CostText"/> — the printed symbols written out several times — but life and a
    /// count of objects to sacrifice or discard have no text to repeat, so the multiplier itself
    /// travels here and the engine multiplies as it asks.
    /// <para>
    /// On the event rather than re-read from the board at answer time, because the board moves:
    /// the counter that priced the offer can be gone by the time the player answers, and a replay
    /// has to reach the same price the game actually offered. One for every offer that never
    /// scaled at all, which is nearly all of them.
    /// </para>
    /// </remarks>
    public int Times { get; init; } = 1;

    public override string Rule => "601.2b";

    public override string Describe() => $"{PlayerId:N} may pay {CostText}.";
}

/// <summary>
/// A delayed triggered ability was created and is waiting for its moment (CR 603.7).
/// </summary>
/// <remarks>
/// "Exile it at the beginning of the next end step" is not a trigger printed on the permanent —
/// it is one an effect <em>creates</em> while resolving, and which fires once, later, and then is
/// gone (CR 603.7b). It is an event rather than a field so a replay rebuilds the pending ability
/// exactly, including which object it was aimed at.
/// <para>
/// <see cref="TurnCreated"/> is what makes "the <em>next</em> end step" mean the next one: an
/// ability created during an end step must not fire in that same end step.
/// </para>
/// </remarks>
public sealed record DelayedTriggerCreated(
    Guid Id,
    Guid ControllerId,
    ObjectId SubjectId,
    TurnStep Step,
    string EffectId,
    int TurnCreated) : GameEvent
{
    public override string Rule => "603.7";

    public override string Describe() => $"{EffectId} will happen at the next {Step}.";
}

/// <summary>A delayed triggered ability fired and is done (CR 603.7b).</summary>
public sealed record DelayedTriggerFired(Guid Id) : GameEvent
{
    public override string Rule => "603.7b";

    public override string Describe() => "A delayed trigger fired.";
}

/// <summary>
/// A player is to look at the top cards and keep one, asked at the next settle (CR 701.20a).
/// </summary>
/// <remarks>
/// The impulse-draw shape: "Look at the top four cards of your library. Put one of them into your
/// hand and the rest on the bottom of your library in a random order." Deferred like a scry,
/// because the choice halts the game and this is the last thing its ability does.
/// <para>
/// The order the rest go back in is randomised by the game rather than chosen, so it is recorded
/// as an outcome the same way a shuffle is — the answer to the choice does not determine it.
/// </para>
/// </remarks>
public sealed record LookAndTakeRequested(
    Guid PlayerId,
    int Count,
    Zone Destination,
    Zone RestTo = Zone.Library,
    string FilterId = "any")
    : GameEvent
{
    /// <summary>Whether the cards are revealed to everybody rather than looked at (CR 701.16a).</summary>
    /// <remarks>
    /// "Reveal the top ten cards of your library. Put a creature card from among them onto the
    /// battlefield" — the same question with the cards face up, which matters because a look kept
    /// private would hide from the opponents which ten cards the choice was made from.
    /// </remarks>
    public bool Reveal { get; init; }

    /// <summary>+1/+1 counters the taken card arrives with, when it lands on the battlefield.</summary>
    public int CountersOnTaken { get; init; }

    /// <summary>
    /// A generated continuous effect given to the taken card as it lands, or null for none —
    /// "it gains hexproof until your next turn" as a <c>grant:</c> definition id.
    /// </summary>
    public string? TakenGrantId { get; init; }

    /// <summary>Whether the grant lasts until the taker's next turn rather than this one (CR 611.2b).</summary>
    public bool GrantUntilTakersNextTurn { get; init; }

    /// <summary>
    /// Whether the library is shuffled after the rest go back — "then shuffle" — instead of the
    /// rest going to the bottom in a random order (CR 701.20a).
    /// </summary>
    public bool ShuffleAfter { get; init; }

    /// <summary>How many of what was seen may be taken, or null for as many as match.</summary>
    public int? TakeLimit { get; init; } = 1;

    /// <summary>Whether every match is taken with no question asked (CR 118.3).</summary>
    public bool TakeAll { get; init; }

    /// <summary>
    /// A ceiling on the mana value of what may be taken, or null for none.
    /// </summary>
    /// <remarks>
    /// Already a number by the time it reaches here: "with mana value X or less" is settled
    /// against the resolution that raised the question, so a replayed request offers the same
    /// cards rather than re-reading an X that has gone.
    /// </remarks>
    public int? MaxManaValue { get; init; }

    /// <summary>Whether a taken card arrives on the battlefield tapped (CR 701.26a).</summary>
    public bool TappedOnTaken { get; init; }

    /// <summary>
    /// The permanent whose ability is doing the looking, when the taken card must remember it.
    /// </summary>
    /// <remarks>
    /// Hideaway only. It travels on the request rather than being looked up at the settle, for
    /// the reason every other field here does: the answer arrives a priority later, and by then
    /// the resolution that knew whose ability this was has finished.
    /// </remarks>
    public ObjectId? Source { get; init; }

    public override string Rule => "701.20a";

    public override string Describe() =>
        $"{PlayerId:N} looks at the top {Count} and takes "
            + (TakeAll ? "every match" : TakeLimit is { } limit ? $"up to {limit}" : "any number")
            + $" to {Destination}.";
}

/// <summary>
/// A player is to search their library, asked at the next settle (CR 701.23).
/// </summary>
/// <remarks>
/// <paramref name="FilterId"/> is a <em>name</em> — "basic-land", "Forest" — rather than a
/// predicate, for the same reason a generated continuous effect is named: a delegate cannot be
/// folded from a log, and the name is what makes a stored game legible when it is read back.
/// <para>
/// What the search turns up is hidden information: a library may not be looked at (CR 401.2), and
/// searching is the exception that lets its owner and nobody else see it. The options therefore go
/// only to the player being asked, which <see cref="Views.PlayerViewProjector"/> already
/// guarantees for every choice.
/// </para>
/// </remarks>
/// <param name="MaxManaValue">
/// The largest mana value the search may fetch, or null for no limit (CR 202.3). A tutor that
/// ignored the cap its card prints would fetch anything, which is a strictly better card.
/// </param>
/// <param name="ExactManaValue">
/// The one mana value the search may fetch, or null. Transmute asks for "the same mana value as
/// this card", which is a different question from a cap - a cheaper card is no more fetchable
/// than a dearer one.
/// </param>
public sealed record LibrarySearchRequested(
    Guid PlayerId,
    string FilterId,
    Zone Destination,
    bool Tapped,

    /// <summary>
    /// How many cards may be found, for "search your library for up to two basic land cards".
    /// </summary>
    /// <remarks>
    /// A ceiling and not a quota: CR 701.23c lets a player fail to find however many they like,
    /// so one is the common case and the number only ever raises the limit.
    /// </remarks>
    int Count = 1,
    int? MaxManaValue = null,

    /// <summary>
    /// The lowest mana value a found card may have, for "with mana value N or greater".
    /// </summary>
    /// <remarks>
    /// A floor beside the cap rather than a signed range, because a card names one or the other
    /// and never both - and two nullable numbers say "no limit" without a sentinel.
    /// </remarks>
    int? MinManaValue = null,
    int? ExactManaValue = null) : GameEvent
{
    /// <summary>
    /// Which zones this one instruction reaches (CR 701.23a).
    /// </summary>
    /// <remarks>
    /// An init property rather than another positional parameter, so that every log already
    /// written reads back as the library search it was.
    /// </remarks>
    public SearchIn Zones { get; init; } = SearchIn.Library;

    /// <summary>
    /// Whose zones are being searched, when they are not <see cref="PlayerId"/>'s own.
    /// </summary>
    /// <remarks>
    /// The two are separate because an extraction searches an opponent's zones and the
    /// <em>caster</em> chooses what is found. Which player is asked is <see cref="PlayerId"/>,
    /// and that is what decides who sees the options — a hand searched this way is shown to the
    /// searcher and to nobody else, which is the whole of how the hidden information is modelled.
    /// The cards are never put into a view; the question is, and
    /// <see cref="Views.PlayerViewProjector"/> already sends a question only to the player it is
    /// for.
    /// </remarks>
    public Guid? ZonesOf { get; init; }

    /// <summary>Whose cards are being looked through.</summary>
    public Guid Searched => ZonesOf ?? PlayerId;

    public override string Rule => "701.23";

    public override string Describe() =>
        ZonesOf is { } them
            ? $"{PlayerId:N} searches {them:N}'s {Zones} for a {FilterId} card."
            : $"{PlayerId:N} searches their {Zones} for a {FilterId} card.";
}

/// <summary>
/// A player is to seek a card, performed at the next settle.
/// </summary>
/// <remarks>
/// No <see cref="GameEvent.Rule"/>, and that is deliberate: seeking is a digital-only keyword
/// action that the Comprehensive Rules do not define, so there is no paragraph to point at.
/// <para>
/// The card is chosen by the game rather than by its controller, which is why this is settled
/// rather than asked - there is no question to put to anybody. Like every other random outcome
/// here, what reaches the log is the moves the seek made, not the roll that made them, so a
/// replay agrees without re-rolling.
/// </para>
/// </remarks>
public sealed record SeekRequested(
    Guid PlayerId,
    string FilterId,
    Zone Destination,
    bool Tapped,
    int Count = 1,
    int? MaxManaValue = null,
    int? MinManaValue = null,
    int? ExactManaValue = null) : GameEvent
{
    public override string Describe() =>
        $"{PlayerId:N} seeks {Count} {FilterId} card(s) to {Destination}.";
}

/// <summary>Modes were chosen for a modal spell as it was cast (CR 601.2b).</summary>
public sealed record ModesChosen(ObjectId StackId, ImmutableList<int> Modes) : GameEvent
{
    public override string Rule => "601.2b";

    public override string Describe() => $"Mode(s) {string.Join(", ", Modes)} chosen.";
}

/// <summary>A spell's squad cost was paid, this many times (CR 702.157a).</summary>
public sealed record SpellSquadded(ObjectId StackId, int Times) : GameEvent
{
    public override string Rule => "702.157a";

    public override string Describe() => $"Squad paid {Times} time(s).";
}

/// <summary>Cards were spliced onto an Arcane spell as it was cast (CR 702.47a).</summary>
/// <remarks>
/// The cards, not their text. A log has to be able to rebuild the stack object from nothing, and
/// an effect list cannot be written down - so what is recorded is which cards were revealed, and
/// their effects are looked up again at resolution exactly as the spell's own are.
/// </remarks>
public sealed record CardsSpliced(
    ObjectId StackId, ImmutableList<Domain.Models.CardDefinition> Cards) : GameEvent
{
    public override string Rule => "702.47a";

    public override string Describe() =>
        $"{string.Join(", ", Cards.Select(c => c.Name))} spliced onto the spell.";
}

/// <summary>A spell's kicker cost was paid as it was cast (CR 702.33d).</summary>
public sealed record SpellKicked(ObjectId StackId) : GameEvent
{
    public override string Rule => "702.33d";

    public override string Describe() => "The spell was kicked.";
}

/// <summary>
/// Which of a spell's several kicker costs were paid, by their printed text (CR 702.33f).
/// </summary>
/// <remarks>
/// "Kicker [A] and/or [B]" is two kicker abilities (CR 702.33b), and the clauses that read the
/// payment back — "if it was kicked with its [A] kicker" — are each linked to one of them
/// (CR 607.2). The flag <see cref="SpellKicked"/> records cannot answer <em>which</em>, so the
/// costs paid ride here beside it, printed exactly as the card prints them, because the clause
/// that asks names the cost in the same spelling. Emitted alongside the flag, never instead of
/// it: every "if it was kicked" card reads the flag, and a spell kicked with either cost has
/// been kicked (CR 702.33d).
/// </remarks>
public sealed record SpellKickedWith(ObjectId StackId, ImmutableList<string> Costs) : GameEvent
{
    public override string Rule => "702.33f";

    public override string Describe() =>
        $"The spell was kicked with {string.Join(" and ", Costs)}.";
}

/// <summary>Its controller declared they would pay the bargain cost (CR 702.166b).</summary>
/// <remarks>
/// Its own event rather than a second meaning for <see cref="SpellKicked"/>: the two are the same
/// shape and different abilities, and a card could in principle print both. Linked abilities
/// (CR 607.2) are exactly the thing that goes wrong when two costs share one flag.
/// </remarks>
public sealed record SpellBargained(ObjectId StackId) : GameEvent
{
    public override string Rule => "702.166b";

    public override string Describe() => "The spell was bargained.";
}

/// <summary>A spell's cleave cost was paid as it was cast (CR 702.148a).</summary>
/// <remarks>
/// The fact that chooses which reading resolves: a cleave card is compiled twice — with the
/// bracketed words and without them — and paying the cleave cost is what selects the second.
/// In the log rather than only in the in-process table, so a replayed game folds the choice
/// back onto the stack object and resolves the same spell the table would have.
/// </remarks>
public sealed record SpellCleaved(ObjectId StackId) : GameEvent
{
    public override string Rule => "702.148a";

    public override string Describe() => "The spell was cast for its cleave cost.";
}

/// <summary>
/// A spell's gift was promised to an opponent as it was cast (CR 702.174a, 702.174k).
/// </summary>
/// <remarks>
/// The promise and the recipient are one event because the rules make them one act: paying the
/// gift cost <em>is</em> choosing an opponent. Who was chosen has to be in the log — the
/// delivery resolves later, an enters trigger may read it later still, and by then the choice
/// is otherwise nowhere.
/// </remarks>
public sealed record GiftPromised(ObjectId StackId, Guid Opponent) : GameEvent
{
    public override string Rule => "702.174k";

    public override string Describe() => $"A gift was promised to {Opponent:N}.";
}

/// <summary>A player is to proliferate, asked at the next settle (CR 701.34a).</summary>
public sealed record ProliferateRequested(Guid PlayerId) : GameEvent
{
    public override string Rule => "701.34";

    public override string Describe() => $"{PlayerId:N} proliferates.";
}

/// <summary>
/// A player must choose a permanent of their own, asked at the next settle (CR 609.4).
/// </summary>
/// <remarks>
/// The karoo lands' bounce, and sacrifice-as-an-effect. Like the optional payment, it carries a
/// <em>locator</em> — which permanent, which of its abilities, which effect inside it — rather
/// than the filter itself, because a filter is a delegate and a delegate is not something a log
/// can rebuild. The set of legal choices is worked out again from the card when the answer is
/// needed.
/// </remarks>
public sealed record ChoosePermanentRequested(
    Guid PlayerId,
    ObjectId SourceId,
    string? AbilityId,
    int EffectIndex,
    string Prompt) : GameEvent
{
    public override string Rule => "609.4";

    public override string Describe() => $"{PlayerId:N} must choose: {Prompt}";
}

/// <summary>A coin flip is owed, and will be made at the next settle (CR 705.2).</summary>
public sealed record CoinFlipRequested(
    Guid PlayerId, ObjectId SourceId, string? AbilityId, int EffectIndex) : GameEvent
{
    /// <summary>The object the trigger that called for the flip was about (CR 603.2).</summary>
    /// <remarks>
    /// Carried for the same reason the optional payment carries it: the branch runs against the
    /// permanent behind the ability, and a permanent has no ability and so no subject.
    /// </remarks>
    public ObjectId? SubjectObject { get; init; }

    /// <summary>
    /// How much the event was about, for a trigger that says "that many" (CR 603.2).
    /// </summary>
    /// <remarks>
    /// Damage dealt, life gained, counters put on — the number the triggering event carried. It
    /// travels with the trigger rather than being looked up again on resolution, because by then
    /// the event is over and nothing in the state remembers how much it was.
    /// </remarks>
    public int? SubjectAmount { get; init; }

    public override string Rule => "705.2";

    public override string Describe() => $"{PlayerId:N} flips a coin.";
}

/// <summary>
/// A coin came down (CR 705.2).
/// </summary>
/// <remarks>
/// The outcome is in the log, not the roll that produced it — the same rule the shuffle follows.
/// A replay reads the result rather than re-flipping, so a stored game cannot come out differently
/// on a different machine or a different .NET version.
/// </remarks>
public sealed record CoinFlipped(Guid PlayerId, bool Won) : GameEvent
{
    public override string Rule => "705.2";

    public override string Describe() => $"{PlayerId:N} {(Won ? "won" : "lost")} the flip.";
}

/// <summary>A die roll is owed, and will be made at the next settle (CR 706.1).</summary>
/// <param name="Sides">How many sides the die has — 20 for a d20 (CR 706.1a).</param>
public sealed record DiceRollRequested(
    Guid PlayerId, ObjectId SourceId, string? AbilityId, int EffectIndex, int Sides) : GameEvent
{
    /// <summary>The object the trigger that called for the roll was about (CR 603.2).</summary>
    /// <remarks>Carried for the reason <see cref="CoinFlipRequested"/> carries its twin.</remarks>
    public ObjectId? SubjectObject { get; init; }

    /// <summary>
    /// What the ability that called for the roll was aimed at (CR 601.2c, 601.2f).
    /// </summary>
    /// <remarks>
    /// Carried rather than looked up, which is the same lesson
    /// <see cref="OptionalPaymentRequested.Targets"/> records and the same way it was learnt.
    /// The rows are run after the ability has resolved, against the <em>permanent</em> whose
    /// ability it was — and a permanent carries no targets, because the ability on the stack
    /// did. "Choose target creature, then roll a d20" therefore rolled its die, ran the row the
    /// number landed in, and dealt its damage to nobody: a Treasure appeared, the creature
    /// stood there, and no event in the log said anything had gone wrong.
    /// </remarks>
    public ImmutableList<Target> Targets { get; init; } = [];

    /// <summary>
    /// How many extra dice replacement effects have added to this roll (CR 706.2b). Each extra
    /// die is rolled alongside the printed one and the lowest results are ignored, which per
    /// CR 706.6 means they never happened: one die comes out of the roll however many went in.
    /// </summary>
    public int ExtraDice { get; init; }

    public override string Rule => "706.1";

    public override string Describe() => $"{PlayerId:N} rolls a d{Sides}.";
}

/// <summary>
/// A die came down (CR 706.2).
/// </summary>
/// <remarks>
/// The outcome is in the log, not the roll that produced it — the same rule the shuffle and the
/// coin flip follow. A replay reads the number rather than re-rolling, so a stored game cannot
/// come out differently on a different machine or a different .NET version.
/// </remarks>
/// <param name="Natural">
/// The number on the die's face before any modifier (CR 706.2). No modifier machinery exists yet,
/// so this always equals <paramref name="Result"/> today — both are recorded so that the day
/// modifiers land, "a die's highest natural result" keeps reading the face and not the sum.
/// </param>
/// <param name="Result">The final result of the roll, after every modifier (CR 706.2).</param>
public sealed record DiceRolled(Guid PlayerId, int Sides, int Natural, int Result) : GameEvent
{
    public override string Rule => "706.2";

    public override string Describe() => $"{PlayerId:N} rolled a d{Sides}: {Result}.";
}

/// <summary>
/// One Attraction was visited by a roll that matched a number lit up on it (CR 701.52a).
/// </summary>
/// <remarks>
/// It changes no state and exists so a visit ability can trigger, which is the same reason
/// <see cref="CombatDamageDealt"/> exists: the roll is one <see cref="DiceRolled"/> and the
/// question each Attraction asks — "is <em>my</em> light the one that came up" — cannot be
/// answered from it without every Attraction's trigger re-deriving the lights, the controller and
/// the board from a number. Said once, per Attraction, by the thing that knows.
/// <para>
/// The lights are <em>not</em> on this event. Which Attraction was visited is the whole of the
/// fact; the numbers behind it are printed on the card the id names, and a copy here could
/// disagree with them.
/// </para>
/// </remarks>
public sealed record AttractionVisited(Guid PlayerId, ObjectId AttractionId) : GameEvent
{
    public override string Rule => "701.52a";

    public override string Describe() => $"{PlayerId:N} visited {AttractionId}.";
}

/// <summary>Energy counters gained or spent by a player (CR 122.1, 107.14).</summary>
/// <param name="TurnIncrease">
/// Whether this is the once-a-turn automatic increase, which may not happen twice (CR 702.179b).
/// Starting your engines is not one, so a player who starts them and then makes an opponent lose
/// life goes from no speed to 2 in the same turn.
/// </param>
public sealed record SpeedChanged(Guid PlayerId, int Speed, bool TurnIncrease = false) : GameEvent
{
    public override string Rule => "702.179";

    public override string Describe() => $"{PlayerId:N} is now at speed {Speed}.";
}

public sealed record EnergyChanged(Guid PlayerId, int Delta) : GameEvent
{
    // Was 107.4c, which is the colorless mana symbol {C} and has nothing to do with energy. The
    // citation gate only checks that a rule exists, and that one does.
    public override string Rule => "107.14";

    public override string Describe() =>
        $"{PlayerId:N} {(Delta >= 0 ? "gets" : "pays")} {Math.Abs(Delta)} energy.";
}

/// <summary>
/// Experience counters gained by a player (CR 122.1).
/// </summary>
/// <remarks>
/// Energy's shape, and a delta rather than a total for the same reason: two effects giving a
/// counter in the same window each say what they did, so neither has to have read the other's
/// result first. The delta is signed only because the type is - nothing in the corpus removes an
/// experience counter, and the fold clamps at zero the way energy's does rather than trusting
/// that.
/// </remarks>
public sealed record ExperienceCountersChanged(Guid PlayerId, int Delta) : GameEvent
{
    // CR 122.1 and no further: unlike poison, loyalty, shield and the rest, an experience counter
    // has no sub-rule because it does nothing by itself. It is a marker on a player that the
    // cards which hand them out read back, and that is the whole of it.
    public override string Rule => "122.1";

    public override string Describe() =>
        $"{PlayerId:N} gets {Math.Abs(Delta)} experience counter(s).";
}

/// <summary>
/// One player must choose a card from another player's revealed hand (CR 701.16).
/// </summary>
/// <remarks>
/// The Duress shape, and the only question in the game where the player answering it is not the
/// player it is about — which is why the chooser and the owner are both recorded. Reading the
/// owner off the source's controller would work for exactly the cards where they are the same
/// person and quietly pick the wrong hand everywhere else.
/// </remarks>
/// <remarks>
/// The filter is a filter and the prompt is words. They were one field until a card asked for an
/// "instant or sorcery card": the engine was deciding what a player could pick by looking for the
/// substring "nonland" in the sentence it was going to show them, so every kind that was not
/// nonland silently offered the whole hand.
/// </remarks>
/// <summary>
/// A player arranged cards in their library into a chosen order (CR 701.19a).
/// </summary>
/// <remarks>
/// Distinct from a shuffle even though the state change is the same, because the log is what a
/// game is replayed and explained from: "the library is now in this order" and "the library was
/// randomised" are different facts, and a reader who cannot tell them apart cannot tell a player
/// arranging their deck from the game doing it for them.
/// <para>
/// Not a move, and that is the point: cards staying in one zone do not change identity, so the
/// order is stated outright rather than performed as a sequence of moves that would mint a new id
/// for each card and invalidate the ones still to be placed (CR 400.7).
/// </para>
/// </remarks>
public sealed record LibraryOrdered(
    Guid PlayerId, ImmutableList<ObjectId> Order) : GameEvent
{
    public override string Rule => "701.19";

    public override string Describe() => $"{PlayerId:N} arranges their library.";
}

/// <summary>
/// A player has to arrange cards they have looked at, back on top (CR 701.19a).
/// </summary>
public sealed record LibraryOrderRequested(
    Guid PlayerId, ImmutableList<ObjectId> Cards) : GameEvent
{
    public override string Rule => "701.19";

    public override string Describe() => $"{PlayerId:N} orders {Cards.Count} card(s).";
}

/// <summary>
/// A player has to choose one permanent from a set, which then takes counters (CR 701.36a).
/// </summary>
/// <remarks>
/// Bolster and amass are one shape with different candidate sets - "the creature with the least
/// toughness among creatures you control", "an Army you control" - and the same ending. The
/// candidates are worked out when the effect resolves and carried here, so the answer does not
/// have to recompute a set that may have changed while the question was outstanding.
/// </remarks>
public sealed record CounterChoiceRequested(
    Guid ChooserId,
    ImmutableList<ObjectId> Candidates,
    string Kind,
    int Count) : GameEvent
{
    public override string Rule => "701.36";

    public override string Describe() =>
        $"{ChooserId:N} chooses where {Count} {Kind} counter(s) go.";
}

/// <summary>
/// A player has to choose which permanents to untap, up to a limit (CR 701.21a).
/// </summary>
/// <remarks>
/// "Untap up to three lands" names no target, so the choice is made as the effect resolves and
/// the candidates are whatever is tapped at that moment. Carried in the request rather than
/// recomputed on the answer: a land that untaps some other way in between is no longer a
/// candidate, and the list is what the player was actually offered.
/// </remarks>
public sealed record UntapChoiceRequested(
    Guid ChooserId,
    ImmutableList<ObjectId> Candidates,
    int Most) : GameEvent
{
    public override string Rule => "701.21";

    public override string Describe() => $"{ChooserId:N} may untap up to {Most}.";
}

/// <summary>
/// A player has to name a colour before an effect can finish (CR 700.2, 202.2).
/// </summary>
/// <remarks>
/// The request carries what the answer is *for*, rather than a locator back into the card. A
/// colour choice does exactly one thing to exactly one set of permanents, so there is nothing to
/// look up afterwards - unlike an optional payment, where the answer decides which branch of the
/// card runs and only the card knows what those branches are.
/// </remarks>
/// <summary>A player must name a creature type for a permanent (CR 205.1b).</summary>
public sealed record CreatureTypeChoiceRequested(
    Guid ChooserId,
    ObjectId SourceId,
    ImmutableList<ObjectId> Affected) : GameEvent
{
    public override string Rule => "205.1b";

    public override string Describe() => $"{ChooserId:N} chooses a creature type.";
}

public sealed record ColorChoiceRequested(
    Guid ChooserId,
    ObjectId SourceId,
    ImmutableList<ObjectId> Affected,
    ColorChoiceUse Use) : GameEvent
{
    public override string Rule => "700.2";

    public override string Describe() => $"{ChooserId:N} chooses a colour.";
}

/// <summary>
/// A player must name one of the five basic land types for a land (CR 305.6, 305.7).
/// </summary>
/// <remarks>
/// The creature type's request with one field more, and that field is the rule: CR 305.7 gives
/// two different answers to "this land is now a Swamp" depending on whether the sentence said
/// "in addition to its other types", and only the card knows which it said. Carried on the
/// request rather than worked out when the answer arrives, because by then the sentence is gone.
/// <para>
/// The lands it applies to are captured here rather than re-derived on the answer, for the reason
/// every deferred question here captures its subject: the question is asked after the resolution
/// that raised it, and a land that has left in between is not one of the lands that were chosen
/// for.
/// </para>
/// </remarks>
public sealed record LandTypeChoiceRequested(
    Guid ChooserId,
    ObjectId SourceId,
    ImmutableList<ObjectId> Affected,
    bool InAddition) : GameEvent
{
    public override string Rule => "305.7";

    public override string Describe() => $"{ChooserId:N} chooses a basic land type.";
}

/// <summary>
/// A player must name the colour of mana an effect is adding, as it resolves (CR 106.1a).
/// </summary>
/// <remarks>
/// The question the effect vocabulary had nowhere to put. "Add one mana of any color" outside a
/// mana ability is a choice made on resolution, and until this existed the sentence was left
/// unread rather than guessed at - the mana-ability path answers the same question by splitting
/// itself into one ability per colour, which an effect cannot do.
/// <para>
/// It takes the shape every other mid-resolution question here takes: an event, then a
/// <see cref="State.ChoiceKind"/>, so a replay reaches the same offer and the answer is read back
/// out of the log rather than out of a captured continuation.
/// </para>
/// <para>
/// The menu rides on the event rather than being worked out again when the answer arrives.
/// "One mana of any type that land produced" is read off a permanent that may have left the
/// battlefield by then, and a question whose options changed underneath it is not the question
/// that was asked. <see cref="ManaColor.Colorless"/> in the list means colourless mana, which is
/// a type of its own and not an absence of colour (CR 106.1b).
/// </para>
/// </remarks>
public sealed record ManaColorChoiceRequested(
    Guid PlayerId,
    ImmutableList<ManaColor> Options,
    int Amount = 1,
    ObjectId? SourceId = null) : GameEvent
{
    public override string Rule => "106.1a";

    public override string Describe() =>
        $"{PlayerId:N} chooses a color for {Amount} mana.";
}

/// <summary>What a named colour is then used for.</summary>
public enum ColorChoiceUse
{
    /// <summary>"…gains protection from the color of your choice until end of turn."</summary>
    ProtectionFrom,

    /// <summary>"…becomes the color of your choice until end of turn."</summary>
    BecomesColor,
}

public sealed record HandChoiceRequested(
    Guid ChooserId,
    Guid OwnerId,
    ObjectId SourceId,
    string? AbilityId,
    int EffectIndex,
    string Prompt,
    string FilterId = Abilities.SearchFilters.AnyCard,

    /// <summary>
    /// Where the chosen card goes, when the asking effect is not one the resolver can look back
    /// up. Defaulted to the graveyard so the discard-from-an-opponent's-hand path is unchanged.
    /// </summary>
    Zone Destination = Zone.Graveyard,

    /// <summary>Whether the chooser may decline - "you <em>may</em> put".</summary>
    bool Optional = false,

    /// <summary>Whether a card put onto the battlefield arrives tapped.</summary>
    bool Tapped = false) : GameEvent
{
    public override string Rule => "701.16";

    public override string Describe() => $"{ChooserId:N} chooses from {OwnerId:N}'s hand.";
}

/// <summary>A prevention shield on a player was created, or spent (CR 615.1).</summary>
public sealed record PlayerPreventionChanged(Guid PlayerId, int Delta) : GameEvent
{
    public override string Rule => "615.1";

    public override string Describe() => Delta > 0
        ? $"{Delta} damage will be prevented from {PlayerId:N}."
        : $"{-Delta} damage was prevented.";
}

/// <summary>Poison counters given to a player (CR 122.1, 704.5c).</summary>
public sealed record PoisonCountersChanged(Guid PlayerId, int Delta) : GameEvent
{
    public override string Rule => "122.1";

    public override string Describe() => $"{PlayerId:N} got {Delta} poison counter(s).";
}

/// <summary>A spell's kicker cost was paid more than once (CR 702.33c).</summary>
/// <remarks>
/// Separate from <c>SpellKicked</c> rather than a count added to it, because that event is in
/// every log this engine has ever written and changing its shape would make the old ones
/// unreadable. Both are emitted for a multikicked spell: the flag every existing card asks for,
/// and the number the few that need it read.
/// </remarks>
public sealed record SpellMultikicked(ObjectId Id, int Times) : GameEvent
{
    public override string Rule => "702.33";

    public override string Describe() => $"{Id} was kicked {Times} time(s).";
}

/// <summary>A player got the city's blessing (CR 702.131a).</summary>
/// <remarks>
/// Its own event rather than a flag set quietly, because it is permanent and one-way: a replay
/// that could not see the moment it was gained could not reproduce any game containing it.
/// </remarks>
public sealed record CitysBlessingGained(Guid PlayerId) : GameEvent
{
    public override string Rule => "702.131";

    public override string Describe() => $"{PlayerId:N} got the city's blessing.";
}

/// <summary>A player became the monarch (CR 725.3).</summary>
/// <remarks>
/// One event for the whole change, because becoming the monarch is also the previous monarch
/// ceasing to be one - a single fact about the game rather than two about two players. Its own
/// event rather than a flag set quietly, for the same reason the city's blessing has one: a
/// replay that could not see the moment could not reproduce the game.
/// </remarks>
public sealed record MonarchChanged(Guid PlayerId) : GameEvent
{
    public override string Rule => "725.3";

    public override string Describe() => $"{PlayerId:N} became the monarch.";
}

/// <summary>A player took the initiative (CR 726.3).</summary>
/// <remarks>
/// The monarch's twin: one event for the whole change, because taking the initiative is also the
/// previous holder ceasing to have it (CR 726.3). It is emitted even when the taker already has
/// it — CR 726.5 says taking it again is a real taking that triggers the Undercity venture, just
/// not a second designation — so the log records the taking and the fold makes the re-assignment
/// harmless.
/// </remarks>
public sealed record InitiativeTaken(Guid PlayerId) : GameEvent
{
    public override string Rule => "726.3";

    public override string Describe() => $"{PlayerId:N} took the initiative.";
}

/// <summary>
/// A prevention effect was created by a resolving spell or ability (CR 615.1).
/// </summary>
/// <remarks>
/// CR 615.3: nothing restricts casting a spell that generates one, and the effect lasts until it
/// is used up or its duration expires — which for these is the turn (CR 514.2).
/// <para>
/// The whole effect travels on the event rather than an id into a registry, because unlike a
/// continuous effect there is nothing about it that a delegate has to answer — what it shields
/// and what it prevents are both data. That keeps the fold a copy rather than a lookup, so the
/// state and the log cannot describe different shields.
/// </para>
/// </remarks>
public sealed record PreventionEffectCreated(PreventionEffect Effect) : GameEvent
{
    public override string Rule => "615.1";

    public override string Describe() =>
        (Effect?.Amount is { } amount ? $"{amount} damage" : "All damage")
        + " will be prevented.";
}

/// <summary>
/// A shield that only ever stopped one instance of damage has stopped it (CR 615.8).
/// </summary>
/// <remarks>
/// The end of a prevention effect, and the only one the engine has: every other shield in
/// <see cref="State.GameState.Preventions"/> is swept away by the turn ending, which the cleanup
/// fold does without an event because it can see the turn number. This one ends on something
/// that happened, so it has to be recorded — the state is a fold of the log, and "the Circle has
/// been used" is not derivable from the damage event alone.
/// <para>
/// Emitted <em>by the replacement that prevented the damage</em>, in the same batch, because
/// CR 615.8's "once an instance of damage from that source has been prevented" is a fact about
/// that one application. Written any later, a second simultaneous damage event from the same
/// source would meet a shield that had already done its job.
/// </para>
/// </remarks>
public sealed record PreventionEffectSpent(Guid EffectId) : GameEvent
{
    public override string Rule => "615.8";

    public override string Describe() => "The prevention shield is used up.";
}

/// <summary>
/// A resolving effect is waiting to be told which source its shield names (CR 609.7b).
/// </summary>
/// <remarks>
/// The question the Circles of Protection ask, in the shape every question this engine asks
/// takes: an event plus a <see cref="State.ChoiceKind"/>, so a replay reaches the same offer and
/// the answer is read back out of the log rather than out of a captured continuation.
/// <see cref="ManaColorChoiceRequested"/> is the closest model, and this follows it in both
/// respects.
/// <para>
/// <strong>The shield rides on the event, whole but for its source.</strong> Everything the
/// sentence settled — what it shields, which damage it watches, whether it is spent by the first
/// instance — was worked out while the spell resolved, and a shield rebuilt when the answer
/// arrives would be rebuilt from a board that has moved. Only <see cref="PreventionEffect.Source"/>
/// is left empty, and it is the one field the answer fills.
/// </para>
/// <para>
/// <strong>The menu rides on it too</strong>, for the reason the mana menu does: "a red source of
/// your choice" was read off a battlefield that an opponent may have changed by the time anybody
/// answers, and a question whose options moved underneath it is not the question that was asked.
/// The properties are kept on the shield as well as used to build the list, because CR 615.9 has
/// them rechecked when the damage would happen — a source that has since stopped being red is
/// not prevented, and does not spend the shield.
/// </para>
/// </remarks>
public sealed record DamageSourceChoiceRequested(
    Guid PlayerId,
    PreventionEffect Shield,
    ImmutableList<ObjectId> Options) : GameEvent
{
    public override string Rule => "609.7b";

    public override string Describe() =>
        $"{PlayerId:N} chooses a source of damage to prevent.";
}

/// <summary>
/// A resolving spell or ability said some damage can't be prevented (CR 615.12).
/// </summary>
/// <remarks>
/// The mirror of <see cref="PreventionEffectCreated"/> and carries the same record, because a
/// ban and a shield describe damage in the same words — "damage can't be prevented this turn"
/// against "prevent all damage this turn". What differs is which side of the prevention pass
/// reads it.
/// </remarks>
public sealed record UnpreventableDamageDeclared(PreventionEffect Damage) : GameEvent
{
    public override string Rule => "615.12";

    public override string Describe() =>
        (Damage?.Kind == DamageKind.Combat ? "Combat damage" : "Damage") + " can't be prevented.";
}

/// <summary>A resolving spell or ability stopped a player gaining life (CR 119.7).</summary>
public sealed record LifeGainBanned(LifeGainBan Ban) : GameEvent
{
    public override string Rule => "119.7";

    public override string Describe() => "Life can't be gained.";
}

/// <summary>A prevention shield was created, or spent (CR 615.7).</summary>
public sealed record PreventionChanged(ObjectId Id, int Delta) : GameEvent
{
    public override string Rule => "615.1";

    public override string Describe() =>
        Delta > 0 ? $"{Delta} damage will be prevented from {Id}." : $"{-Delta} damage was prevented.";
}

/// <summary>
/// A permanent was turned face up or face down (CR 707.9).
/// </summary>
/// <remarks>
/// Turning a permanent face up is a special action: it does not use the stack and cannot be
/// responded to (CR 707.9a). So this is the whole of it — there is no ability to put anywhere,
/// and by the time an opponent sees the event it has already happened.
/// </remarks>
/// <summary>A creature with exploit may sacrifice a creature (CR 702.110a).</summary>
public sealed record ExploitRequested(Guid ChooserId, ObjectId ExploiterId) : GameEvent
{
    public override string Rule => "702.110a";

    public override string Describe() => $"{ExploiterId} may exploit a creature.";
}

/// <summary>A creature with exploit sacrificed one (CR 702.110b).</summary>
/// <remarks>
/// Its own event because the second half of every exploit card asks for it - "when this creature
/// exploits a creature, ..." - and a plain sacrifice cannot answer: sacrifices happen for all
/// sorts of reasons and nothing about the move says which of them this was.
/// </remarks>
public sealed record CreatureExploited(ObjectId ExploiterId, ObjectId SacrificedId) : GameEvent
{
    public override string Rule => "702.110b";

    public override string Describe() => $"{ExploiterId} exploited {SacrificedId}.";
}

/// <summary>
/// A soulbond ability resolved and its controller may pair (CR 702.95a).
/// </summary>
/// <remarks>
/// <paramref name="PartnerId"/> is the entering creature when the trigger was "whenever another
/// creature you control enters" — that arm pairs the newcomer with this creature and nothing
/// else, so the question is yes or no. Null for the "when this creature enters" arm, where the
/// player chooses among every unpaired creature they control. Both halves are re-checked when
/// the question is actually asked, because CR 702.95c re-tests creature, battlefield and
/// controller at resolution and answers "neither becomes paired" if any fails.
/// </remarks>
public sealed record SoulbondPairRequested(Guid ChooserId, ObjectId SourceId, ObjectId? PartnerId)
    : GameEvent
{
    public override string Rule => "702.95a";

    public override string Describe() => $"{SourceId} may pair with another creature.";
}

/// <summary>Two creatures became paired by a soulbond ability (CR 702.95b).</summary>
public sealed record CreaturesPaired(ObjectId FirstId, ObjectId SecondId) : GameEvent
{
    public override string Rule => "702.95b";

    public override string Describe() => $"{FirstId} became paired with {SecondId}.";
}

/// <summary>A pairing came apart (CR 702.95e).</summary>
/// <remarks>
/// A real event rather than an absence, for the reason unattaching is: the pairing is state on
/// both creatures, and a replay has to reach the same board. Emitted by the sweep in
/// state-based actions when a break-up condition holds; the fold clears whichever halves still
/// point at each other, so a half that already left the battlefield needs nothing cleared.
/// </remarks>
public sealed record CreaturesUnpaired(ObjectId FirstId, ObjectId SecondId) : GameEvent
{
    public override string Rule => "702.95e";

    public override string Describe() => $"{FirstId} and {SecondId} became unpaired.";
}

/// <summary>A player must choose a creature token of theirs to copy (CR 701.36a).</summary>
public sealed record PopulateRequested(Guid ChooserId) : GameEvent
{
    public override string Rule => "701.36a";

    public override string Describe() => $"{ChooserId:N} populates.";
}

/// <summary>A player must pick which of the top two cards to manifest (CR 701.62a).</summary>
/// <remarks>
/// The two cards are named in the event rather than read off the library when the question is
/// asked: by then something else may have moved them, and the player is choosing between the
/// two they looked at.
/// </remarks>
public sealed record ManifestDreadRequested(
    Guid ChooserId, System.Collections.Immutable.ImmutableArray<ObjectId> Looked) : GameEvent
{
    public override string Rule => "701.62a";

    public override string Describe() => $"{ChooserId:N} looks at {Looked.Length} to manifest one.";
}

/// <summary>
/// A player must pick which of the things this resolution touched to move (CR 608.2c).
/// </summary>
/// <remarks>
/// The candidates are named in the event rather than worked out again when the question is
/// asked, for the reason a manifest dread's two cards are: by then the record that produced them
/// is gone, the resolution having ended, and the player is choosing among the cards that
/// sentence actually put there.
/// <para>
/// One zone for all of them, because every verb the record answers pins where the object went -
/// milled and destroyed to a graveyard, exiled to exile - so a set gathered under one participle
/// is a set in one place. A candidate that has since moved is dropped when the question is asked
/// rather than filtered here, the same way every other owed question re-checks its options.
/// </para>
/// </remarks>
public sealed record TouchedChoiceRequested(
    Guid ChooserId,
    System.Collections.Immutable.ImmutableArray<ObjectId> Candidates,
    Zone From,
    Zone To,
    MoveCause Cause,
    int Least,
    int Most) : GameEvent
{
    public override string Rule => "608.2c";

    public override string Describe() =>
        $"{ChooserId:N} picks up to {Most} of {Candidates.Length} to move to {To}.";
}

/// <summary>The Ring tempted a player (CR 701.54a).</summary>
public sealed record RingTempted(Guid PlayerId) : GameEvent
{
    public override string Rule => "701.54a";

    public override string Describe() => $"The Ring tempted {PlayerId:N}.";
}

/// <summary>A player chose their Ring-bearer (CR 701.54a).</summary>
public sealed record RingBearerChosen(Guid PlayerId, ObjectId Creature) : GameEvent
{
    public override string Rule => "701.54a";

    public override string Describe() => $"{Creature} is {PlayerId:N}'s Ring-bearer.";
}

/// <summary>A player must choose a creature to bear the Ring (CR 701.54a).</summary>
public sealed record RingBearerRequested(Guid PlayerId) : GameEvent
{
    public override string Rule => "701.54a";

    public override string Describe() => $"{PlayerId:N} chooses a Ring-bearer.";
}

/// <summary>A player's venture marker moved into a room (CR 309.4a, 701.49b).</summary>
/// <remarks>
/// One event for both halves of the keyword action: putting a dungeon into the command zone puts
/// the marker on its topmost room, and every later venture moves it along an arrow. The room
/// ability watches this and nothing else (CR 309.4c), so entering a dungeon fires its first room
/// by the same route that advancing fires the next - two events would have been two chances for
/// one of them not to.
/// <para>
/// It carries the dungeon's name as well as the room's because room names are not unique across
/// dungeons and a log has to say which board it was about.
/// </para>
/// </remarks>
public sealed record VentureMarkerMoved(Guid PlayerId, string Dungeon, string Room) : GameEvent
{
    public override string Rule => "701.49b";

    public override string Describe() => $"{PlayerId:N} ventured into {Room}.";
}

/// <summary>A player has to choose which arrow to follow out of a room (CR 701.49b).</summary>
/// <remarks>
/// The question, not the answer. A dungeon forks, and CR 701.49b has the player choose which of
/// the arrows pointing away from their room to follow - so the venture cannot finish inside the
/// effect that called for it, exactly like a scry.
/// </remarks>
public sealed record VentureRoomRequested(
    Guid PlayerId, string Dungeon, ImmutableList<string> Rooms) : GameEvent
{
    public override string Rule => "701.49b";

    public override string Describe() => $"{PlayerId:N} chooses a room to venture into.";
}

/// <summary>A player completed a dungeon as its card left the game (CR 309.7).</summary>
/// <remarks>
/// Completing is not entering the last room: CR 309.6 removes the dungeon card from the game as a
/// state-based action once the marker is on the bottommost room and no room ability of that
/// dungeon is still on the stack, and CR 309.7 says the player completes it as that happens. So a
/// card that reads "whenever you complete a dungeon" fires after the last room's ability has
/// resolved, not before it.
/// </remarks>
public sealed record DungeonCompleted(Guid PlayerId, string Dungeon) : GameEvent
{
    public override string Rule => "309.7";

    public override string Describe() => $"{PlayerId:N} completed {Dungeon}.";
}

/// <summary>A clash was called for and has to be carried out (CR 701.30a).</summary>
/// <remarks>
/// Carries a locator back into the card, the way a coin flip does: the "if you win" half is
/// effects, which cannot travel in a log, so the request says where to find them again.
/// </remarks>
public sealed record ClashRequested(
    Guid PlayerId, ObjectId SourceId, string? AbilityId, int EffectIndex) : GameEvent
{
    /// <summary>The object the triggering event was about, carried into the branch.</summary>
    public ObjectId? SubjectObject { get; init; }

    public override string Rule => "701.30a";

    public override string Describe() => $"{PlayerId:N} clashes.";
}

/// <summary>A card already in a library was put on the bottom of it (CR 701.30a).</summary>
/// <remarks>
/// Not a move: the card does not change zones, so it is not a new object (CR 400.7) and nothing
/// that was watching it should stop. Reaching for <c>Move</c> here made a fresh id for a card
/// that had never left, and the soak found a table where something then asked after the old one.
/// </remarks>
public sealed record CardPutOnBottom(Guid PlayerId, ObjectId Id) : GameEvent
{
    public override string Rule => "701.30a";

    public override string Describe() => $"{Id} went to the bottom of {PlayerId:N}'s library.";
}

/// <summary>A card was revealed from the top of a library for a clash (CR 701.30a).</summary>
public sealed record ClashRevealed(Guid PlayerId, ObjectId Id, int ManaValue) : GameEvent
{
    public override string Rule => "701.30a";

    public override string Describe() => $"{PlayerId:N} revealed {Id} ({ManaValue}).";
}

/// <summary>An exiled card may be played until the stated turn ends (CR 601.3e).</summary>
/// <param name="ThroughOwnersNextTurn">
/// Whether the window runs to the end of the owner's next turn rather than to the end of this
/// one. The two are different durations and the longer one cannot be written as a turn number.
/// </param>
public sealed record CardMayBePlayed(
    ObjectId Id, int UntilTurn, bool ThroughOwnersNextTurn = false) : GameEvent
{
    public override string Rule => "601.3e";

    public override string Describe() => $"{Id} may be played through turn {UntilTurn}.";
}

/// <summary>
/// A resolving spell asks to be exiled with time counters instead of reaching the graveyard.
/// </summary>
/// <remarks>
/// Requested rather than done, because the spell is still on the stack as the effect resolves and
/// only stops being that object when it leaves (CR 400.7). The counters belong on whatever it
/// becomes, which does not exist yet.
/// </remarks>
public sealed record SuspendOnResolveRequested(ObjectId SubjectId, int Counters) : GameEvent
{
    public override string Rule => "702.61a";

    public override string Describe() => $"{SubjectId} will be exiled with {Counters} time counters.";
}

/// <summary>
/// A permanent will be sacrificed unless its controller pays a cost that is not mana.
/// </summary>
/// <remarks>
/// One request and one choice, rather than a yes/no followed by a selection: picking nothing is
/// how the player declines, and picking the whole cost is how they pay. That keeps the whole
/// decision in a single answer, which is what the log has to be able to replay.
/// </remarks>
public sealed record SacrificeUnlessPaidRequested(
    Guid PlayerId,
    ObjectId SubjectId,
    ChosenCostKind Kind,
    int Count,
    string FilterId) : GameEvent
{
    public override string Rule => "701.17a";

    public override string Describe() =>
        $"{PlayerId:N} sacrifices {SubjectId} unless they pay {Count} {FilterId}.";
}

/// <summary>A resolving spell asks to be exiled encoded on a creature (CR 702.99a).</summary>
/// <remarks>
/// Requested for the same reason the self-suspend is: the card is still the spell on the stack
/// while its own last clause resolves, and only becomes the exiled card a moment later under a
/// new id (CR 400.7).
/// </remarks>
public sealed record CipherRequested(ObjectId SubjectId, Guid PlayerId) : GameEvent
{
    public override string Rule => "702.99a";

    public override string Describe() => $"{PlayerId:N} may encode {SubjectId} on a creature.";
}

/// <summary>An exiled card was encoded on a creature (CR 702.99b).</summary>
public sealed record SpellEncoded(ObjectId CardId, ObjectId CreatureId) : GameEvent
{
    public override string Rule => "702.99b";

    public override string Describe() => $"{CardId} is encoded on {CreatureId}.";
}

/// <summary>A permanent's owner must choose which end of their library it goes to.</summary>
public sealed record LibraryEndChoiceRequested(Guid PlayerId, ObjectId SubjectId) : GameEvent
{
    public override string Rule => "701.18a";

    public override string Describe() => $"{PlayerId:N} chooses an end of their library for {SubjectId}.";
}

/// <summary>Damage headed for one permanent will be dealt to another instead (CR 614.1b).</summary>
public sealed record RedirectionChanged(ObjectId Id, int Delta, ObjectId? To) : GameEvent
{
    public override string Rule => "614.1b";

    public override string Describe() => To is { } where
        ? $"{Delta} damage to {Id} will be dealt to {where} instead."
        : $"{Id} redirects {Delta} less damage.";
}

/// <summary>A spell was cast as a prototyped spell (CR 718.3).</summary>
public sealed record SpellPrototyped(ObjectId StackId) : GameEvent
{
    public override string Rule => "718.3";

    public override string Describe() => $"{StackId} was cast prototyped.";
}

/// <summary>An Assassin or a commander connected, enabling freerunning (CR 702.173a).</summary>
public sealed record FreerunningEnabled(Guid PlayerId) : GameEvent
{
    public override string Rule => "702.173a";

    public override string Describe() => $"{PlayerId:N} may pay freerunning costs this turn.";
}

/// <summary>A player will take an extra turn after this one (CR 500.7).</summary>
public sealed record ExtraTurnCreated(Guid PlayerId) : GameEvent
{
    public override string Rule => "500.7";

    public override string Describe() => $"{PlayerId:N} takes an extra turn after this one.";
}

/// <summary>An extra turn was taken off the queue and is beginning (CR 500.7).</summary>
public sealed record ExtraTurnTaken(Guid PlayerId) : GameEvent
{
    public override string Rule => "500.7";

    public override string Describe() => $"{PlayerId:N} takes their extra turn.";
}

/// <summary>Mana just added keeps its place through the emptying (CR 500.4).</summary>
/// <param name="Kept">
/// Exactly what was added, not the whole pool: a player who already had mana keeps only what
/// this ability produced, and carrying the pool would hand them the rest for free.
/// </param>
public sealed record ManaMadePersistent(
    Guid PlayerId, ManaPool Kept, ManaPersistence Until) : GameEvent
{
    public override string Rule => "500.4";

    public override string Describe() => $"{PlayerId:N} keeps {Kept} until {Until}.";
}

/// <summary>A mana permission ran out, so the pool empties normally again (CR 500.4).</summary>
public sealed record ManaPersistenceEnded(Guid PlayerId) : GameEvent
{
    public override string Rule => "500.4";

    public override string Describe() => $"{PlayerId:N} no longer keeps mana between steps.";
}

/// <summary>A player may play extra lands this turn (CR 505.6b).</summary>
public sealed record ExtraLandDropGranted(Guid PlayerId, int Count) : GameEvent
{
    public override string Rule => "505.6b";

    public override string Describe() => $"{PlayerId} may play {Count} more land(s) this turn.";
}

/// <summary>A permanent gained the prepared designation (CR 722.3a).</summary>
public sealed record BecamePrepared(ObjectId Id) : GameEvent
{
    public override string Rule => "722.3a";

    public override string Describe() => $"{Id} is prepared.";
}

/// <summary>A permanent lost the prepared designation (CR 722.3b).</summary>
public sealed record Unprepared(ObjectId Id) : GameEvent
{
    public override string Rule => "722.3b";

    public override string Describe() => $"{Id} is no longer prepared.";
}

/// <summary>A longer play window ran out (CR 601.3e).</summary>
public sealed record PlayWindowClosed(ObjectId Id) : GameEvent
{
    public override string Rule => "601.3e";

    public override string Describe() => $"{Id} can no longer be played.";
}

/// <summary>The spell was cast for its escape cost (CR 702.139a).</summary>
public sealed record SpellEscaped(ObjectId Id) : GameEvent
{
    public override string Rule => "702.139a";

    public override string Describe() => $"{Id} escaped.";
}

/// <summary>The spell's warp cost was paid (CR 702.185a).</summary>
public sealed record SpellWarped(ObjectId Id) : GameEvent
{
    public override string Rule => "702.185a";

    public override string Describe() => $"{Id} was cast for its warp cost.";
}

/// <summary>A warped permanent was exiled and may be cast again later (CR 702.185a).</summary>
public sealed record CardWarpedToExile(ObjectId Id, int Turn) : GameEvent
{
    public override string Rule => "702.185a";

    public override string Describe() => $"{Id} was warped away on turn {Turn}.";
}

/// <summary>A player must discover - exile until a cheap enough nonland (CR 701.57a).</summary>
public sealed record DiscoverRequested(Guid PlayerId, ObjectId SourceId, int AtMost) : GameEvent
{
    public override string Rule => "701.57a";

    public override string Describe() => $"{PlayerId:N} discovers {AtMost}.";
}

/// <summary>A permanent connived and its controller owes a discard (CR 701.50a).</summary>
/// <remarks>
/// The draw happens as the effect resolves and the discard cannot: which card goes is the
/// player's to say, and whether it was a land decides the counter. So the effect emits this,
/// the question is asked in the settle sweep, and the answer does the rest - the same shape
/// every other choice an effect cannot make for itself uses.
/// </remarks>
public sealed record ConniveRequested(Guid ChooserId, ObjectId SourceId) : GameEvent
{
    public override string Rule => "701.50a";

    public override string Describe() => $"{ChooserId:N} connives with {SourceId}.";
}

/// <summary>A card was put onto the battlefield face down as a 2/2 (CR 701.40a).</summary>
/// <remarks>
/// Its own event rather than a <see cref="PermanentTurned"/>, because being manifested is a
/// second fact about the permanent and not only that it is face down: it decides which procedure
/// turns it face up.
/// </remarks>
public sealed record CardManifested(ObjectId Id) : GameEvent
{
    public override string Rule => "701.40a";

    public override string Describe() => $"{Id} was manifested.";
}

public sealed record PermanentTurned(ObjectId Id, bool FaceDown) : GameEvent
{
    public override string Rule => "707.9";

    public override string Describe() => $"{Id} turned {(FaceDown ? "face down" : "face up")}.";
}

/// <summary>It became day, or night (CR 731.1).</summary>
/// <remarks>
/// Its own event because the game can only gain the designation, never lose it: once it has
/// become one of the two it is one of them for the rest of the game, and a replay that could not
/// see the moment could not reproduce which.
/// </remarks>
public sealed record DayNightChanged(bool IsDay) : GameEvent
{
    public override string Rule => "731.1";

    public override string Describe() => IsDay ? "It became day." : "It became night.";
}

/// <summary>A permanent turned to its other face (CR 712.8d).</summary>
/// <remarks>
/// The face index rather than a "transformed" flag, because a card may have more than two faces
/// and because a replay should not have to work out which face a name belonged to. What the
/// reducer does with it is swap the object's characteristics for that face's - so everything
/// downstream reads the face it is on without knowing that faces exist.
/// </remarks>
public sealed record PermanentTransformed(ObjectId Id, int FaceIndex) : GameEvent
{
    public override string Rule => "712.8d";

    public override string Describe() => $"{Id} turned to face {FaceIndex}.";
}

/// <summary>
/// A spell on the stack was copied (CR 707.10).
/// </summary>
/// <remarks>
/// The copy carries its own id and the targets it was made with, rather than being described as
/// "a copy of that object": the thing it copies will have left the stack by the time anything
/// replays this, and a log has to be readable on its own.
/// <para>
/// Deliberately not an <c>ObjectCreated</c> followed by a <c>TargetsChosen</c>. Copying does not
/// choose targets — a copy has the same ones (CR 707.10a) — and announcing that it did would fire
/// every "becomes the target" trigger a second time, ward included.
/// </para>
/// </remarks>
public sealed record SpellCopied(
    ObjectId Id,
    Domain.Models.CardDefinition Card,
    Guid ControllerId,
    System.Collections.Immutable.ImmutableList<Abilities.Target> Targets) : GameEvent
{
    public override string Rule => "707.10";

    public override string Describe() => $"A copy of {Card.Name} was put on the stack.";
}

/// <summary>A card was exiled by a permanent that will give it back (CR 400.7).</summary>
public sealed record ExiledUntilLeaves(ObjectId Id, ObjectId By) : GameEvent
{
    public override string Rule => "400.7";

    public override string Describe() => $"{Id} is exiled by {By}.";
}

/// <summary>A spell was cast with its buyback cost paid (CR 702.27a).</summary>
public sealed record SpellBoughtBack(ObjectId Id) : GameEvent
{
    public override string Rule => "702.27";

    public override string Describe() => $"{Id} was cast with buyback.";
}

/// <summary>A card was exiled from hand to be cast for nothing later (CR 702.169a).</summary>
public sealed record CardPlotted(ObjectId Id, int Turn) : GameEvent
{
    public override string Rule => "702.169";

    public override string Describe() => $"{Id} was plotted on turn {Turn}.";
}

/// <summary>A card was exiled with time counters on it (CR 702.62a).</summary>
public sealed record CardSuspended(
    ObjectId Id, Guid PlayerId, int TimeCounters) : GameEvent
{
    public override string Rule => "702.62";

    public override string Describe() =>
        $"{PlayerId:N} suspends {Id} with {TimeCounters} time counter(s).";
}

/// <summary>A time counter came off a suspended card (CR 702.62a).</summary>
public sealed record TimeCounterRemoved(ObjectId Id, int Remaining) : GameEvent
{
    public override string Rule => "702.62";

    public override string Describe() => $"{Id} has {Remaining} time counter(s) left.";
}

/// <summary>Cards were shown to every player (CR 701.16a).</summary>
public sealed record CardsRevealed(
    Guid PlayerId, ImmutableList<ObjectId> Cards) : GameEvent
{
    public override string Rule => "701.16";

    public override string Describe() =>
        $"{PlayerId:N} reveals {Cards.Count} card(s).";
}

/// <summary>One player looked at another's hand (CR 701.19a).</summary>
/// <remarks>
/// Deliberately not <see cref="CardsRevealed"/>. Revealing shows the cards to everybody; looking
/// shows them to one player. Nothing reads either event today, so the two would behave alike -
/// which is exactly why the distinction has to be in the log rather than left for later: a look
/// recorded as a reveal becomes an information leak the moment the view layer starts reading it.
/// </remarks>
public sealed record HandLookedAt(
    Guid ViewerId, Guid PlayerId, ImmutableList<ObjectId> Cards) : GameEvent
{
    public override string Rule => "701.19a";

    public override string Describe() =>
        $"{ViewerId:N} looks at {PlayerId:N}'s {Cards.Count} card(s).";
}

/// <summary>A spell was cast with its offspring cost paid (CR 702.171a).</summary>
public sealed record SpellOffspring(ObjectId Id) : GameEvent
{
    public override string Rule => "702.171";

    public override string Describe() => $"{Id} was cast with offspring.";
}

/// <summary>A spell was cast as an Aura for its bestow cost (CR 702.103a).</summary>
public sealed record SpellBestowed(ObjectId Id) : GameEvent
{
    public override string Rule => "702.103";

    public override string Describe() => $"{Id} was cast with bestow.";
}

/// <summary>A spell was cast for its mutate cost (CR 702.140a).</summary>
/// <remarks>
/// Recorded on the spell rather than remembered by the engine, for the reason buyback and bestow
/// are: the spell has to still know, several priority passes later, that it is a mutating
/// creature spell — and a rebuilt game has to know it too, or the merge becomes an ordinary
/// creature arriving on the battlefield beside the creature it should have joined.
/// </remarks>
public sealed record SpellMutating(ObjectId Id, bool OnTop) : GameEvent
{
    public override string Rule => "702.140a";

    public override string Describe() =>
        $"{Id} was cast for its mutate cost, going {(OnTop ? "over" : "under")}.";
}

/// <summary>
/// A mutating creature spell merged with the creature it targeted (CR 702.140c, 730.2).
/// </summary>
/// <remarks>
/// One event for the whole merge, because it is one thing happening: the spell leaves the stack
/// and <em>becomes part of</em> the permanent (CR 730.2b), which is not the card ceasing to exist
/// and not a zone change either. Splitting it into a removal and an addition would leave a moment
/// between them in which the card was nowhere.
/// <para>
/// The permanent keeps its id: CR 730.2c says it is the same object it was, so it has not just
/// entered, has not just come under anybody's control, and every continuous effect on it goes on
/// applying. That is also why nothing here touches its timestamp.
/// </para>
/// </remarks>
public sealed record PermanentMutated(ObjectId Id, ObjectId SpellId, bool OnTop) : GameEvent
{
    public override string Rule => "702.140c";

    public override string Describe() =>
        $"{SpellId} merged {(OnTop ? "over" : "under")} {Id}.";
}

/// <summary>
/// A mutating creature spell stopped being one, because its target had become illegal
/// (CR 702.140b).
/// </summary>
/// <remarks>
/// The rule is an exception to CR 608.2b, and it is written as the spell <em>changing</em> rather
/// than as the engine making an exception: "it ceases to be a mutating creature spell and
/// continues resolving as a creature spell". Emitting that change means a replayed game reaches
/// the same fork by reaching the same events, instead of re-deciding it from a target legality
/// that has moved on since.
/// </remarks>
public sealed record SpellMutationLapsed(ObjectId Id) : GameEvent
{
    public override string Rule => "702.140b";

    public override string Describe() =>
        $"{Id} lost its mutate target and resolves as a creature spell.";
}

/// <summary>
/// A merged permanent is leaving the battlefield, so its components become separate objects
/// (CR 730.3).
/// </summary>
/// <remarks>
/// "One permanent leaves the battlefield and each of the individual components are put into the
/// appropriate zone." The topmost component travels as the permanent itself, through the ordinary
/// move that follows this — so only the cards <em>under</em> it are named here, each with the id
/// it will have in its new zone (CR 400.7).
/// <para>
/// The ids are on the event rather than made by the reducer, because a fold decides nothing: two
/// replays of one log have to produce the same objects.
/// </para>
/// </remarks>
public sealed record MergedPermanentSeparated(
    ObjectId Id, Zone To, ImmutableList<ObjectId> ComponentIds) : GameEvent
{
    public override string Rule => "730.3";

    public override string Describe() =>
        $"{Id} came apart into {ComponentIds.Count} more card(s) in {To}.";
}

/// <summary>A spell was cast for its overload cost (CR 702.96a).</summary>
public sealed record SpellOverloaded(ObjectId Id) : GameEvent
{
    public override string Rule => "702.96";

    public override string Describe() => $"{Id} was overloaded.";
}

/// <summary>
/// A spell was cast for its sneak cost, in place of an attacker (CR 702.190a).
/// </summary>
/// <remarks>
/// Carries what the returned creature was attacking, because that is the only moment the game
/// knows: the creature is in its owner's hand before the spell resolves, and the permanent this
/// becomes has to arrive attacking the same defender (CR 702.190b).
/// </remarks>
public sealed record SpellSneaked(ObjectId Id, State.AttackTarget? Against) : GameEvent
{
    public override string Rule => "702.190";

    public override string Describe() => $"{Id} was sneaked in.";
}

/// <summary>
/// A permanent phased out (CR 702.26b).
/// </summary>
/// <remarks>
/// Carries whose untap step brings it back, which is not always its controller's: an Aura that
/// phased out along with what it enchants returns with that permanent (CR 702.26g).
/// </remarks>
/// <summary>A permanent stopped attacking or blocking without leaving the battlefield (CR 506.4).</summary>
/// <remarks>
/// Its own event rather than a flag, because being in combat is state and state moves only by
/// an event here. Phasing out already did this as part of leaving the battlefield; this is the
/// same removal for a permanent that stays where it is - a Gustcloak stepping out of a block,
/// and CR 506.4c is what makes that stop the damage.
/// <para>
/// Only the combat lists change. The creatures that were blocking it are still blocking
/// creatures (CR 509.1h), and they now block nothing, so they assign no combat damage - which
/// falls out of the attacker no longer being there rather than being written down twice.
/// </para>
/// </remarks>
public sealed record RemovedFromCombat(ObjectId Id) : GameEvent
{
    public override string Rule => "506.4";

    public override string Describe() => $"{Id} was removed from combat.";
}

public sealed record PermanentPhasedOut(ObjectId Id, Guid ReturnsFor) : GameEvent
{
    public override string Rule => "702.26b";

    public override string Describe() => $"{Id} phased out.";
}

/// <summary>A permanent phased in (CR 702.26c).</summary>
public sealed record PermanentPhasedIn(ObjectId Id) : GameEvent
{
    public override string Rule => "702.26c";

    public override string Describe() => $"{Id} phased in.";
}

/// <summary>A spell's teamwork cost was paid as it was cast (CR 702.194b).</summary>
public sealed record SpellTeamwork(ObjectId Id) : GameEvent
{
    public override string Rule => "702.194";

    public override string Describe() => $"{Id} was cast using teamwork.";
}

/// <summary>A spell was cast for its awaken cost (CR 702.113a).</summary>
public sealed record SpellAwakened(ObjectId Id) : GameEvent
{
    public override string Rule => "702.113";

    public override string Describe() => $"{Id} was awakened.";
}

/// <summary>A spell was cast for its evoke cost (CR 702.74a).</summary>
public sealed record SpellEvoked(ObjectId Id) : GameEvent
{
    public override string Rule => "702.74";

    public override string Describe() => $"{Id} was cast for its evoke cost.";
}

/// <summary>A spell was cast for its dash cost (CR 702.109a).</summary>
public sealed record SpellDashed(ObjectId Id) : GameEvent
{
    public override string Rule => "702.109";

    public override string Describe() => $"{Id} was cast with dash.";
}

/// <summary>A spell was cast for its blitz cost, or the permanent it became (CR 702.152a).</summary>
/// <remarks>
/// Carries an object id rather than a player, because blitz is a fact about one object and is
/// asked of that object twice: on the stack, to know what to hand the permanent, and on the
/// battlefield, to know whether the permanent draws a card when it dies.
/// </remarks>
public sealed record SpellBlitzed(ObjectId Id) : GameEvent
{
    public override string Rule => "702.152";

    public override string Describe() => $"{Id} was cast with blitz.";
}

/// <summary>A card was exiled face down to be cast on a later turn (CR 702.143a).</summary>
public sealed record CardForetold(ObjectId Id, int Turn) : GameEvent
{
    public override string Rule => "702.143";

    public override string Describe() => $"{Id} was foretold on turn {Turn}.";
}

/// <summary>A player was given permission to cast a card for free (CR 601.2b).</summary>
/// <param name="ToHandIfDeclined">
/// Whether the card goes to its owner's hand when the offer lapses, rather than staying where it
/// is. Discover says so and cascade does not, and the difference is the whole of what separates
/// the two (CR 701.57a, CR 702.85a).
/// </param>
/// <param name="Transformed">
/// Whether taking the offer casts the card transformed (CR 712.11a) — a defeated Siege's
/// "you may cast it transformed without paying its mana cost" (CR 310.12b). On the offer rather
/// than derived from the card, because the same battle card in exile under a cascade offer is
/// cast as its front face; only the offer knows which cast it bought.
/// </param>
public sealed record FreeCastOffered(
    ObjectId Id,
    Guid PlayerId,
    string Cost = "",
    bool ToHandIfDeclined = false,
    bool Transformed = false) : GameEvent
{
    public override string Rule => "601.2b";

    public override string Describe() => Cost.Length == 0
        ? $"{PlayerId:N} may cast {Id} without paying."
        : $"{PlayerId:N} may cast {Id} for {Cost}.";
}

/// <summary>The offer lapsed unused (CR 601.2b).</summary>
public sealed record FreeCastLapsed(ObjectId Id) : GameEvent
{
    public override string Rule => "601.2b";

    public override string Describe() => $"The offer on {Id} lapsed.";
}

/// <summary>
/// A player may cast one card from their hand for nothing (CR 601.2b).
/// </summary>
/// <remarks>
/// The offer <see cref="FreeCastOffered"/> cannot make, and the difference is which of the two
/// knows what card it is about. That one names an object, because the effect that makes it has
/// exiled or milled a particular card and the permission is a fact about that card. This one
/// names a description - "a spell with mana value 3 or less" - and the card is chosen by the
/// player at the moment they cast, so the permission has to be a fact about the player.
/// <para>
/// The whole offer travels on the event rather than an id into a registry, for the reason
/// <see cref="PreventionEffectCreated"/> carries its shield: what it covers is data, so the fold
/// is a copy rather than a lookup and the state cannot describe a different offer from the log.
/// </para>
/// </remarks>
public sealed record HandCastOffered(State.HandCastOffer Offer) : GameEvent
{
    public override string Rule => "601.2b";

    public override string Describe() =>
        $"{Offer?.PlayerId:N} may cast a card from hand without paying.";
}

/// <summary>An offer to cast from hand has been taken (CR 601.2b).</summary>
/// <remarks>
/// **The event that makes the permission spent rather than standing.** Without it the offer sits
/// on the player until they pass, and an Expertise that casts one spell would cast every spell
/// in the hand that answered its description - strictly better than the printed card, and a
/// thing no test that only casts once can see.
/// <para>
/// Emitted <em>by the cast that used it</em>, in the same batch and before the cast is announced,
/// exactly as <see cref="PreventionEffectSpent"/> is emitted by the replacement that used the
/// shield: the fact is about that one cast, and written any later a trigger off the cast could
/// find the offer still open and take it again.
/// </para>
/// </remarks>
public sealed record HandCastOfferSpent(Guid OfferId) : GameEvent
{
    public override string Rule => "601.2b";

    public override string Describe() => "The free cast from hand was taken.";
}

/// <summary>An offer to cast from hand lapsed unused (CR 601.2b).</summary>
/// <remarks>
/// The other end, and the one that keeps a permission from outliving its window. It is the same
/// moment <see cref="FreeCastLapsed"/> happens at - the offered player passing priority - because
/// the deviation both share is the same one: the rules give the window inside the resolution, and
/// an engine that cannot cast in the middle of one gives the next pass instead.
/// </remarks>
public sealed record HandCastOfferLapsed(Guid OfferId) : GameEvent
{
    public override string Rule => "601.2b";

    public override string Describe() => "The free cast from hand lapsed.";
}

/// <summary>
/// A player became a battle's protector (CR 310.9).
/// </summary>
/// <remarks>
/// Chosen by the battle's controller as it enters (CR 310.9a) — forced when only one player may
/// be chosen, which is every Siege at a two-player table (CR 310.12a) — and chosen again by the
/// state-based action when the designated protector stops being eligible (CR 704.5x, 704.5y).
/// A battle has one protector at a time (CR 310.9f), so folding this over an earlier choice
/// replaces it.
/// </remarks>
public sealed record ProtectorChosen(ObjectId Id, Guid PlayerId) : GameEvent
{
    public override string Rule => "310.9";

    public override string Describe() => $"{PlayerId:N} now protects {Id}.";
}

/// <summary>A cascading spell is looking for something cheaper (CR 702.85a).</summary>
/// <summary>A shuffle an effect asked for, performed at the next settle (CR 701.24a).</summary>
/// <param name="AlsoShuffleIn">
/// Cards that go into the library before it is shuffled, listed as the effect resolved rather
/// than gathered later. "Shuffle your graveyard into your library" on a sorcery must not shuffle
/// that sorcery in: it is still on the stack while it resolves and only reaches the graveyard
/// afterwards (CR 608.2m), which is exactly the window this request waits through.
/// </param>
/// <remarks>
/// A request rather than the shuffle itself, for the same reason cascade is: the order a library
/// ends up in is the game's decision and has to come from the seeded source, which an effect has
/// no access to. The order reaches the log on the <see cref="LibraryShuffled"/> that answers this.
/// </remarks>
public sealed record ShuffleRequested(
    Guid PlayerId, ImmutableList<ObjectId> AlsoShuffleIn) : GameEvent
{
    public override string Rule => "701.24";

    public override string Describe() => AlsoShuffleIn.IsEmpty
        ? $"{PlayerId:N} shuffles their library."
        : $"{PlayerId:N} shuffles {AlsoShuffleIn.Count} card(s) into their library.";
}

public sealed record CascadeRequested(Guid PlayerId, ObjectId SourceId, int LessThan) : GameEvent
{
    public override string Rule => "702.85";

    public override string Describe() => $"{PlayerId:N} cascades below {LessThan}.";
}

/// <summary>
/// A rippling spell is offering to show the top of its caster's library (CR 702.60a).
/// </summary>
/// <remarks>
/// The reveal is optional and the offer is a question, so this only records that the question is
/// owed - the same shape cascade and every other mid-resolution decision use.
/// </remarks>
public sealed record RippleRequested(Guid PlayerId, ObjectId SourceId, int Count) : GameEvent
{
    public override string Rule => "702.60";

    public override string Describe() => $"{PlayerId:N} may ripple {Count}.";
}

public sealed record PermanentTapped(ObjectId Id) : GameEvent
{
    public override string Rule => "701.26a";

    public override string Describe() => $"{Id} tapped.";
}

/// <summary>
/// Permanents stopped being summoning sick, having been controlled since the turn began
/// (CR 302.6).
/// </summary>
public sealed record SummoningSicknessCleared(ImmutableList<ObjectId> Ids) : GameEvent
{
    public override string Rule => "302.6";

    public override string Describe() => $"{Ids.Count} permanent(s) can attack and tap.";
}

/// <summary>
/// A player used their land drop for the turn (CR 505.6b). Separate from the move that put the
/// land onto the battlefield, because a land can reach the battlefield without being played.
/// </summary>
public sealed record LandDropUsed(Guid PlayerId) : GameEvent
{
    public override string Rule => "505.6b";

    public override string Describe() => $"{PlayerId:N} played a land.";
}

/// <summary>
/// A spell was cast (CR 601.2i): it is on the stack and the casting is complete.
/// </summary>
/// <remarks>
/// The card's move to the stack is a separate <see cref="ObjectMoved"/>. This event is what
/// "whenever a player casts a spell" watches, and it is emitted only once casting has finished —
/// a spell that is still being cast has not been cast.
/// </remarks>
/// <remarks>
/// Where it was cast from is part of the event because by the time anything reads it the card is
/// on the stack and its old zone is gone — the object that was in the graveyard stopped existing
/// when it moved (CR 400.7). A card asking "whenever you cast a spell from anywhere other than
/// your hand" is asking about a fact only the casting knew.
/// </remarks>
/// <summary>
/// Mana was spent casting a spell, and how much the payer had spent this turn (CR 700.14).
/// </summary>
/// <remarks>
/// Its own event rather than a field on <see cref="SpellCastEvent"/>, because "expend N" is a
/// trigger about a <em>threshold being crossed</em> and a trigger predicate is handed the state
/// from one side of the event (CR 603.6) - so the crossing has to be legible in the event itself.
/// Carrying both totals is what makes the predicate one comparison and makes a replayed log
/// answer the same question the live game did.
/// <para>
/// Emitted before the cast is announced, so a "whenever you cast" trigger and an "expend" trigger
/// off the same spell go on the stack in the order the rules put them (CR 603.3b) rather than in
/// the order this file happens to emit them.
/// </para>
/// </remarks>
public sealed record ManaSpentCasting(Guid PlayerId, int Amount, int Before, int After) : GameEvent
{
    public override string Rule => "700.14";

    public override string Describe() =>
        $"{PlayerId:N} spent {Amount} mana casting (now {After} this turn).";
}

public sealed record SpellCastEvent(
    Guid PlayerId,
    ObjectId StackId,
    string CardName,
    Zone From = Zone.Hand) : GameEvent
{
    public override string Rule => "601.2i";

    public override string Describe() => $"{PlayerId:N} cast {CardName}.";
}

/// <summary>
/// The top object of the stack finished resolving (CR 608.2m, 608.3).
/// </summary>
public sealed record StackObjectResolved(ObjectId StackId, string Description) : GameEvent
{
    public override string Rule => "608.2";

    public override string Describe() => $"{Description} resolved.";
}

/// <summary>Marked damage was removed from every permanent (CR 514.2).</summary>
public sealed record DamageCleared : GameEvent
{
    public override string Rule => "514.2";

    public override string Describe() => "Damage removed from all permanents.";
}

/// <summary>A permanent will sit out its controller's next untap step (CR 502.3).</summary>
public sealed record UntapSkipped(ObjectId Id, bool Skipping) : GameEvent
{
    public override string Rule => "502.3";

    public override string Describe() => Skipping
        ? $"{Id} does not untap next untap step."
        : $"{Id} untaps normally again.";
}

/// <summary>Damage removed from one permanent (CR 701.19c).</summary>
/// <remarks>
/// Distinct from <see cref="DamageCleared"/>, which is the cleanup step emptying the whole board.
/// Regeneration and its relatives remove damage from <em>the</em> permanent, and using the global
/// event for that let one creature being regenerated wipe the damage off every other creature
/// too - which is a save for every damaged permanent its controller never paid for.
/// </remarks>
public sealed record DamageRemoved(ObjectId Id) : GameEvent
{
    public override string Rule => "701.19";

    public override string Describe() => $"Damage removed from {Id}.";
}

/// <summary>
/// An object came into existence in a zone rather than moving there from another one.
/// </summary>
/// <remarks>
/// Tokens are the reason this exists (CR 111.1): a token is created on the battlefield and was
/// never anywhere else. Cards conjured or brought in from outside the game (CR 400.11b) arrive
/// the same way. It carries the full card definition so that a log replays without needing
/// anything the log does not contain.
/// </remarks>
public sealed record ObjectCreated(
    ObjectId Id,
    CardDefinition Card,
    Guid OwnerId,
    Guid ControllerId,
    Zone Zone,
    ZonePosition Position = ZonePosition.Top) : GameEvent
{
    public override string Rule => "111.1";

    public override string Describe() => $"{Card.Name} created in {Zone}.";
}

// ---- State-based actions and triggers (slice 3) -------------------------------------------

/// <summary>A player lost the game (CR 104.2). The rule that did it is on the event.</summary>
public sealed record PlayerLost(Guid PlayerId, string Reason, string LosingRule) : GameEvent
{
    public override string Rule => LosingRule;

    public override string Describe() => $"{PlayerId:N} lost: {Reason}.";
}

/// <summary>
/// Every creature that dealt combat damage in one damage step, as a single fact (CR 510.2).
/// </summary>
/// <remarks>
/// The individual damage events say what happened to each recipient; this says what happened
/// at once. "Whenever one or more creatures you control deal combat damage to a player" has to
/// trigger once however many of them connected, and there is no way to get that from a per-
/// creature event without knowing which of them were simultaneous - which is exactly what this
/// records. It changes no state: it exists so a trigger can ask a question the other events
/// cannot answer.
/// </remarks>
public sealed record CombatDamageDealt(
    ImmutableList<ObjectId> Dealers,
    ImmutableList<ObjectId> DealtToPlayer) : GameEvent
{
    public override string Rule => "510.2";

    public override string Describe() => $"{Dealers.Count} creature(s) dealt combat damage.";
}

/// <summary>
/// Every card that left one player's graveyard at once, as a single fact (CR 603.2c).
/// </summary>
/// <remarks>
/// The same shape as <see cref="CombatDamageDealt"/> and for the same reason. "Whenever one or
/// more cards leave your graveyard" is one trigger however many left together (CR 603.2c: an
/// ability triggers once each time its trigger event occurs, and a sentence written in the
/// plural makes the whole batch one occurrence), and there is no way to get that from the
/// individual moves without knowing which of them were simultaneous - which is exactly what this
/// records. Derived from the moves it summarises so the two cannot disagree, and folding to no
/// state change because it exists only for triggers to read.
/// <para>
/// The ids are the <em>new</em> ones: a zone change makes a new object (CR 400.7), and the new
/// one is what exists by the time a trigger asks what kind of card it was.
/// </para>
/// </remarks>
public sealed record CardsLeftGraveyard(
    Guid PlayerId, ImmutableList<ObjectId> Ids) : GameEvent
{
    public override string Rule => "603.2c";

    public override string Describe() =>
        $"{Ids.Count} card(s) left a graveyard.";
}

/// <summary>
/// Every card one player discarded at once, as a single fact (CR 701.9a, 603.2c).
/// </summary>
/// <remarks>
/// The discard twin of <see cref="CardsLeftGraveyard"/>, derived in the same place from the same
/// batch of moves. A discard is a move from a hand to a graveyard and the engine has always
/// recorded it as one; what no single move can say is how many went at once, which is the whole
/// content of "whenever you discard one or more cards" and of the "that many" printed after it.
/// </remarks>
public sealed record CardsDiscarded(
    Guid PlayerId, ImmutableList<ObjectId> Ids) : GameEvent
{
    public override string Rule => "701.9a";

    public override string Describe() => $"{PlayerId:N} discarded {Ids.Count} card(s).";
}

/// <summary>Which colours of mana paid for a spell (CR 202.2, 106.1).</summary>
/// <remarks>
/// Recorded on the spell rather than on the player, because it is a fact about that casting and
/// several cards ask about it after the fact: sunburst counts the colours as the permanent
/// enters, and "if {U} was spent to cast this" is asked when the spell resolves. The pool the
/// mana came from has moved on by then.
/// <para>
/// Colourless is not a colour (CR 106.1b) and is deliberately absent: a sunburst spell paid for
/// with two colourless mana enters with no counters, which is what the card says.
/// </para>
/// </remarks>
public sealed record ManaColorsSpent(ObjectId StackId, ManaPool Spent) : GameEvent
{
    public override string Rule => "202.2";

    public override string Describe() => $"{Spent} spent on {StackId}.";
}

/// <summary>A permanent became monstrous (CR 701.32b).</summary>
/// <remarks>
/// Separate from the counters monstrosity also puts on, because becoming monstrous is what the
/// second activation checks and what the triggers watch for — and a creature can have +1/+1
/// counters without ever having been made monstrous.
/// </remarks>
public sealed record BecameMonstrous(ObjectId Id) : GameEvent
{
    public override string Rule => "701.32b";

    public override string Describe() => $"{Id} became monstrous.";
}

/// <summary>A Mount became saddled (CR 702.171a).</summary>
/// <remarks>
/// Until end of turn, so it ends with damage and the other "until end of turn" effects at the
/// cleanup step (CR 514.2) rather than needing an event of its own to say so.
/// </remarks>
public sealed record PermanentSaddled(ObjectId Id) : GameEvent
{
    public override string Rule => "702.171a";

    public override string Describe() => $"{Id} became saddled.";
}

/// <summary>Damage was marked on a permanent (CR 120.3).</summary>
/// <remarks>
/// Marking is not destroying. Damage sits on the permanent until state-based actions compare it
/// with toughness (CR 704.5g) or cleanup removes it (CR 514.2), which is what lets a creature
/// survive lethal damage if its toughness rises in between.
/// <para>
/// The source is carried because several abilities are about who dealt it rather than how much:
/// lifelink gains its controller that much life (CR 702.15b), and deathtouch is already flagged
/// for the same reason.
/// </para>
/// </remarks>
/// <remarks>
/// Whether it was combat damage is part of the event and not worked out afterwards, for the same
/// reason <see cref="PlayerDamaged"/> carries it: the rules distinguish combat damage from every
/// other kind (CR 510.2), cards ask about the difference, and by the time anything looks the
/// step may have moved on.
/// </remarks>
public sealed record DamageMarked(
    ObjectId Id,
    int Amount,
    bool FromDeathtouch = false,
    ObjectId SourceId = default,
    bool IsCombat = false) : GameEvent
{
    public override string Rule => "120.3";

    public override string Describe() => $"{Amount} damage marked on {Id}.";
}

/// <summary>A door of a split permanent was unlocked (CR 709.5f).</summary>
public sealed record HalfUnlocked(ObjectId Id, int Half) : GameEvent
{
    public override string Describe() => $"{Id} unlocks door {Half}.";
}

/// <summary>A card went on an adventure instead of to a graveyard (CR 715.3d).</summary>
public sealed record WentOnAdventure(ObjectId Id) : GameEvent
{
    public override string Describe() => $"{Id} goes on an adventure.";
}

/// <summary>A creature became renowned (CR 702.112b).</summary>
public sealed record BecameRenowned(ObjectId Id) : GameEvent
{
    public override string Describe() => $"{Id} is renowned.";
}

/// <summary>A Case became solved (CR 719.3a).</summary>
public sealed record CaseSolved(ObjectId Id) : GameEvent
{
    public override string Describe() => $"{Id} is solved.";
}

/// <summary>A Class gained a level (CR 716.2a).</summary>
/// <remarks>
/// The new level, not a delta. A class level bar sets the level to its own number rather than
/// adding one, and writing it as a delta would have made two bars activated in the wrong order
/// produce a level no bar names.
/// </remarks>
public sealed record ClassLevelChanged(ObjectId Id, int Level) : GameEvent
{
    public override string Describe() => $"{Id} becomes level {Level}.";
}

/// <summary>Counters were put on or taken off a permanent (CR 122.1).</summary>
public sealed record CountersChanged(ObjectId Id, string Kind, int Delta) : GameEvent
{
    public override string Rule => "122.1";

    public override string Describe() =>
        $"{(Delta >= 0 ? "Put" : "Removed")} {Math.Abs(Delta)} {Kind} counter(s) on {Id}.";
}

/// <summary>
/// An object stopped existing without going anywhere (CR 704.5d, 608.2m for abilities).
/// </summary>
/// <remarks>
/// Distinct from a move, because there is no destination. A token that leaves the battlefield
/// ceases to exist, and an ability that finishes resolving was never a card and has no graveyard
/// to go to.
/// </remarks>
public sealed record ObjectCeasedToExist(ObjectId Id, Zone From) : GameEvent
{
    public override string Rule => "704.5d";

    public override string Describe() => $"{Id} ceased to exist.";
}

/// <summary>An ability triggered and is waiting to be put on the stack (CR 603.2).</summary>
public sealed record AbilityTriggered(
    ObjectId SourceId,
    string AbilityId,
    string Text,
    Guid ControllerId) : GameEvent
{
    /// <summary>
    /// The player the triggering event was about — what "that player" means (CR 603.2).
    /// </summary>
    /// <remarks>
    /// A trigger is the only kind of ability whose text can point at something the ability never
    /// chose: "whenever this creature deals combat damage to a player, that player discards a
    /// card" names a player decided by the event, not by the controller. It has to be recorded
    /// when the ability triggers and carried to where it resolves, because by then the event is
    /// long past and nothing else in the game says which player it was.
    /// <para>
    /// It is deliberately not a target. A target is chosen, is checked for legality twice, and
    /// can be made illegal by hexproof; this is none of those things, and putting it in the
    /// target list to save a field would have made the board show it as one.
    /// </para>
    /// </remarks>
    public Guid? SubjectPlayer { get; init; }

    /// <summary>
    /// The object the triggering event was about — what "it" means in a trigger (CR 603.2).
    /// </summary>
    /// <remarks>
    /// The twin of the subject player, and recorded for the same reason: a ward trigger has to
    /// counter the spell that targeted it, and nothing in the game says which spell that was once
    /// the event has passed.
    /// <para>
    /// Only templates that write their own ability text may use it. The printed pronoun is not
    /// safe to read from the shared grammar — of the corpus lines saying "that creature", most
    /// are not triggers at all and mean whatever the sentence before targeted.
    /// </para>
    /// </remarks>
    public ObjectId? SubjectObject { get; init; }

    /// <summary>
    /// How much the event was about, for a trigger that says "that many" (CR 603.2).
    /// </summary>
    /// <remarks>
    /// Damage dealt, life gained, counters put on — the number the triggering event carried. It
    /// travels with the trigger rather than being looked up again on resolution, because by then
    /// the event is over and nothing in the state remembers how much it was.
    /// </remarks>
    public int? SubjectAmount { get; init; }

    public override string Rule => "603.2";

    public override string Describe() => $"Triggered: {Text}";
}

/// <summary>
/// A waiting trigger went on the stack (CR 603.3), topmost, in APNAP order (CR 603.3b).
/// </summary>
/// <summary>
/// A triggered ability that needed a target and had none, so it never reached the stack.
/// </summary>
/// <remarks>
/// CR 603.3d. It is recorded rather than dropped silently: from the outside the ability simply
/// does not happen, and a log that does not say why is a log that cannot explain the game.
/// </remarks>
public sealed record TriggerRemovedForNoTargets(
    ObjectId SourceId,
    string AbilityId,
    string Text,
    Guid ControllerId) : GameEvent
{
    public override string Rule => "603.3d";

    public override string Describe() => $"{Text} was removed — no legal targets.";
}

public sealed record TriggerPutOnStack(
    ObjectId Id,
    ObjectId SourceId,
    CardDefinition SourceCard,
    string AbilityId,
    string Text,
    Guid ControllerId) : GameEvent
{
    /// <summary>
    /// What it targets, chosen by its controller as it went on the stack (CR 603.3d).
    /// </summary>
    /// <remarks>
    /// On the event rather than decided at resolution because that is when the rules say the
    /// choice happens: the targets are public the moment the ability is on the stack, which is
    /// what lets an opponent respond to them.
    /// </remarks>
    public System.Collections.Immutable.ImmutableList<Abilities.Target> Targets { get; init; } = [];

    /// <summary>
    /// Which modes its controller picked, for an ability that offers them (CR 603.3c).
    /// </summary>
    /// <remarks>
    /// Chosen before the targets and carried on the same event, because the modes decide what
    /// there is to target: "choose one — destroy target creature; or draw a card" has one target
    /// or none depending on an answer given a moment earlier.
    /// </remarks>
    public System.Collections.Immutable.ImmutableList<int> Modes { get; init; } = [];

    /// <summary>The player the triggering event was about, if it was about one (CR 603.2).</summary>
    public Guid? SubjectPlayer { get; init; }

    /// <summary>The object the triggering event was about, if it was about one (CR 603.2).</summary>
    public ObjectId? SubjectObject { get; init; }

    /// <summary>
    /// The X its source was cast for, for an ability written around one (CR 607.2).
    /// </summary>
    /// <remarks>
    /// A permanent keeps the value announced for the spell that became it, and an ability of
    /// that permanent referring to X means that value. The ability is its own object on the
    /// stack (CR 113.7a) and the permanent it came from may be gone by the time it resolves, so
    /// the number is copied onto the ability as it goes on the stack rather than looked up
    /// again - which is the same reason the triggering event's subject travels here.
    /// <para>
    /// Zero for everything else, which is nearly every trigger: CR 107.3g puts X at zero
    /// wherever it was not announced, and a permanent that was never cast never announced one.
    /// </para>
    /// </remarks>
    public int VariableValue { get; init; }

    /// <summary>
    /// How much the event was about, for a trigger that says "that many" (CR 603.2).
    /// </summary>
    /// <remarks>
    /// Damage dealt, life gained, counters put on — the number the triggering event carried. It
    /// travels with the trigger rather than being looked up again on resolution, because by then
    /// the event is over and nothing in the state remembers how much it was.
    /// </remarks>
    public int? SubjectAmount { get; init; }

    /// <summary>The attack the source joins when this resolves (CR 506.3c) - ninjutsu.</summary>
    public State.AttackTarget? JoiningAgainst { get; init; }

    public override string Rule => "603.3";

    public override string Describe() => $"{Text} went on the stack.";
}

/// <summary>
/// The game is over (CR 104.2a): one player is left, or everyone has lost.
/// </summary>
/// <remarks>
/// <see cref="WinnerId"/> is null for a draw, which is a real outcome — the last two players can
/// lose simultaneously to a state-based action check.
/// </remarks>
public sealed record GameEnded(Guid? WinnerId) : GameEvent
{
    public override string Rule => "104.2a";

    public override string Describe() =>
        WinnerId is null ? "The game ended in a draw." : $"{WinnerId:N} won the game.";
}

// ---- Continuous and replacement effects (slice 4) -----------------------------------------

/// <summary>A resolved spell or ability created a continuous effect (CR 611.2).</summary>
public sealed record ContinuousEffectCreated(
    Guid EffectId,
    string DefinitionId,
    ImmutableList<ObjectId> AffectedIds,
    int? UntilEndOfTurn) : GameEvent
{
    /// <summary>Whose next turn ends it, for "until your next turn" (CR 611.2b).</summary>
    /// <remarks>
    /// An init property rather than a fifth positional parameter: every existing caller says
    /// "until end of turn" and adding a position would have been ninety edits, each of which is a
    /// chance to pass the wrong duration silently.
    /// </remarks>
    public Guid? UntilTurnOf { get; init; }

    /// <summary>
    /// Whose next turn ends it at that turn's <em>end</em>, for "until the end of your next turn"
    /// (CR 611.2b).
    /// </summary>
    /// <remarks>
    /// Set alongside <see cref="UntilEndOfTurn"/> rather than instead of it, and that pairing is
    /// the whole of how the duration works: the number says which turn it began on and the player
    /// says whose later turn ends it, so the cleanup sweep can tell "the end of your next turn"
    /// from "the end of this one" on a turn that is already yours.
    /// </remarks>
    public Guid? UntilEndOfTurnOf { get; init; }

    public override string Rule => "611.2";

    public override string Describe() =>
        $"{DefinitionId} began applying to {AffectedIds.Count} object(s).";
}

/// <summary>
/// A continuous effect ended (CR 514.2 for "until end of turn").
/// </summary>
/// <remarks>
/// "Until end of turn" effects end during the cleanup step, at the same time damage is removed —
/// not at the beginning of the end step, which is a distinction that decides whether a creature
/// pumped this turn survives being blocked.
/// </remarks>
public sealed record ContinuousEffectEnded(Guid EffectId) : GameEvent
{
    public override string Rule => "514.2";

    public override string Describe() => $"Effect {EffectId:N} ended.";
}

/// <summary>
/// An event was replaced by others before it happened (CR 614.1).
/// </summary>
/// <remarks>
/// Recorded for the log's sake: the original event never happened, so nothing triggered off it
/// (CR 603.2g), and without this line the log would show the replacement with no sign of what it
/// replaced.
/// </remarks>
public sealed record EventReplaced(string ReplacedBy, string OriginalDescription) : GameEvent
{
    public override string Rule => "614.1";

    public override string Describe() => $"{OriginalDescription} was replaced by {ReplacedBy}.";
}

// ---- Combat (slice 5) ----------------------------------------------------------------------

/// <summary>
/// The active player declared attackers (CR 508.1). Declaring none is a declaration.
/// </summary>
public sealed record AttackersDeclared(
    ImmutableDictionary<ObjectId, AttackTarget> Attackers) : GameEvent
{
    public override string Rule => "508.1";

    public override string Describe() => $"{Attackers.Count} creature(s) attack.";
}

/// <summary>
/// A creature joined an attack already under way (CR 506.3c).
/// </summary>
/// <remarks>
/// Distinct from <see cref="AttackersDeclared"/>, which is the whole declaration at once. A
/// creature put onto the battlefield attacking was never declared as an attacker and no
/// attack-triggered ability fires for it, so it cannot go through the same event.
/// </remarks>
public sealed record JoinedCombat(ObjectId Id, AttackTarget Target) : GameEvent
{
    public override string Rule => "506.3";

    public override string Describe() => $"{Id} joins the attack.";
}

/// <summary>The defending player declared blockers (CR 509.1).</summary>
public sealed record BlockersDeclared(
    ImmutableDictionary<ObjectId, ImmutableList<ObjectId>> Blockers) : GameEvent
{
    public override string Rule => "509.1";

    public override string Describe() => $"{Blockers.Count} attacker(s) were blocked.";
}

/// <summary>
/// Damage was dealt to a player (CR 120.3). Combat damage is flagged because a great many
/// abilities care specifically about it.
/// </summary>
public sealed record PlayerDamaged(
    Guid PlayerId, ObjectId SourceId, int Amount, bool IsCombat) : GameEvent
{
    public override string Rule => "120.3";

    public override string Describe() =>
        $"{PlayerId:N} was dealt {Amount} {(IsCombat ? "combat " : string.Empty)}damage.";
}

/// <summary>A combat damage step finished (CR 510.2). Recorded so the second one can be found.</summary>
public sealed record CombatDamageStepDone : GameEvent
{
    public override string Rule => "510.2";

    public override string Describe() => "Combat damage was dealt.";
}

/// <summary>Combat ended and everything left it (CR 511.3).</summary>
public sealed record CombatEnded : GameEvent
{
    public override string Rule => "511.3";

    public override string Describe() => "Combat ended.";
}

// ---- Costs, targets and abilities (slice 6) -------------------------------------------------

/// <summary>
/// A state-triggered ability's condition became true, or stopped being true (CR 603.8).
/// </summary>
/// <remarks>
/// The event exists so the fact survives a replay. Whether a state trigger has already fired is
/// not derivable from anything else in the state — the condition being true is exactly the case
/// where it must *not* fire again — so it is recorded rather than recomputed.
/// </remarks>
public sealed record StateTriggerArmed(string Key, bool Armed) : GameEvent
{
    public override string Rule => "603.8";

    public override string Describe() =>
        Armed ? $"{Key} became true." : $"{Key} stopped being true.";
}

/// <summary>
/// A colour or creature type was chosen as a permanent entered (CR 614.12).
/// </summary>
public sealed record CharacteristicChosen(ObjectId Id, string Value) : GameEvent
{
    public override string Rule => "614.12";

    public override string Describe() => $"{Id} chose {Value}.";
}

/// <summary>
/// A card name was chosen as a permanent entered (CR 201.4, 614.12).
/// </summary>
/// <remarks>
/// Its own event beside <see cref="CharacteristicChosen"/> rather than a second use of it,
/// because the two answers land in different fields and only the event knows which was asked.
/// A reducer that had to consult the card to decide where to put the string would be reading
/// the ability source from inside the fold, which is the one thing a fold may not do.
/// </remarks>
public sealed record NameChosen(ObjectId Id, string Value) : GameEvent
{
    public override string Rule => "201.4";

    public override string Describe() => $"{Id} named {Value}.";
}

/// <summary>Mana was added to a player's pool (CR 106.1).</summary>
/// <param name="RestrictedTo">
/// A card filter the mana may only be spent on, beyond the type mask on
/// <paramref name="Restriction"/> — "only to cast Dragon spells".
/// </param>
/// <param name="RestrictedToZone">
/// The zone a spell has to be cast from — "only to cast spells from your graveyard".
/// </param>
/// <param name="RestrictedToCommander">
/// Whether it may only pay for the payer's own commander (CR 903.3).
/// </param>
/// <remarks>
/// The restriction, when there is one, is part of the event rather than something worked out
/// again on replay: it came from the ability that produced the mana, and by the time the log is
/// replayed that ability may be on a permanent that has left the battlefield.
/// <para>
/// The three narrowings sit on the event rather than inside <see cref="ManaRestriction"/> because
/// the pool is what has to remember them: the restriction travels with the individual mana
/// (CR 106.6), and the pool is folded from this event.
/// </para>
/// </remarks>
public sealed record ManaAdded(
    Guid PlayerId,
    ManaColor? Color,
    int Amount,
    ManaRestriction? Restriction = null,
    ObjectId? SourceId = null,
    string? RestrictedTo = null,
    Zone? RestrictedToZone = null,
    bool RestrictedToCommander = false) : GameEvent
{
    public override string Rule => "106.1";

    public override string Describe() =>
        $"{PlayerId:N} added {Amount} {(Color?.ToString() ?? "colourless")} mana"
        + (Restriction is null ? "." : ", restricted.");
}

/// <summary>Mana was spent paying a cost (CR 601.2h).</summary>
public sealed record ManaSpent(Guid PlayerId, ManaPool Remaining) : GameEvent
{
    public override string Rule => "601.2h";

    public override string Describe() => $"{PlayerId:N} paid mana; pool is now {Remaining}.";
}

/// <summary>
/// Unspent mana emptied as a step or phase ended (CR 500.5).
/// </summary>
public sealed record ManaPoolsEmptied : GameEvent
{
    public override string Rule => "500.5";

    public override string Describe() => "Mana pools emptied.";
}

/// <summary>Targets were chosen for a spell or ability on the stack (CR 601.2c).</summary>
/// <remarks>
/// <c>DamageDivision</c> keeps its printed name because it is on two wires: the persisted event
/// log, which is replayed from JSON written by earlier builds, and <c>CastOptionsDto</c>, which
/// clients send by property name. What it carries is a division of <em>anything</em> the spell
/// divides — damage or counters — and the state field it lands in is named for that.
/// </remarks>
public sealed record TargetsChosen(
    ObjectId StackId,
    ImmutableList<Target> Targets,
    int VariableValue,
    ImmutableList<int>? DamageDivision = null) : GameEvent
{
    public override string Rule => "601.2c";

    public override string Describe() => $"{Targets.Count} target(s) chosen.";
}

/// <summary>
/// How an ability on the stack divides its quantity among the targets it chose (CR 601.2d).
/// </summary>
/// <remarks>
/// Its own event rather than a second <see cref="TargetsChosen"/>, because it is a second
/// announcement and not a re-announcement of the first: the targets were chosen when the ability
/// went on the stack and have not changed, and a log that said so twice would read as though they
/// had. A spell never emits one — its division is announced with the cast and arrives inside
/// <see cref="TargetsChosen"/>.
/// </remarks>
public sealed record DivisionAnnounced(ObjectId StackId, ImmutableList<int> Division) : GameEvent
{
    public override string Rule => "601.2d";

    public override string Describe() =>
        "Divided as "
        + string.Join(
            "/", Division.Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        + ".";
}

/// <summary>
/// A spell or ability did not resolve because every target it had was illegal (CR 608.2b).
/// </summary>
/// <remarks>
/// Not the same as being countered by a spell, though the rules call it "countered by game
/// rules" — nothing it would have done happens, including the parts that had nothing to do with
/// the target.
/// </remarks>
public sealed record FizzledForIllegalTargets(ObjectId StackId, string Description) : GameEvent
{
    public override string Rule => "608.2b";

    public override string Describe() => $"{Description} did nothing: every target was illegal.";
}

/// <summary>An activated ability was activated (CR 602.2).</summary>
public sealed record AbilityActivated(
    Guid PlayerId, ObjectId SourceId, string AbilityId, string Text) : GameEvent
{
    public override string Rule => "602.2";

    public override string Describe() => $"{PlayerId:N} activated: {Text}";
}

// ---- Player choices ------------------------------------------------------------------------

/// <summary>
/// The game asked a player to decide something and stopped until they do.
/// </summary>
/// <remarks>
/// Recorded rather than held in a field so a replayed log rebuilds a game that is mid-question
/// exactly as it was — including which player is being asked and what they may pick.
/// </remarks>
public sealed record ChoiceRequested(PendingChoice Choice) : GameEvent
{
    public override string Rule => "103.5";

    public override string Describe() => $"{Choice.PlayerId:N} must choose: {Choice.Prompt}";
}

/// <summary>A player answered (CR 103.5, 603.3b, 616.1, 704.5j).</summary>
public sealed record ChoiceMade(string ChoiceId, ImmutableList<string> Picks) : GameEvent
{
    public override string Describe() => $"Chose {string.Join(", ", Picks)}.";
}

/// <summary>Opening hands are dealt and the mulligan procedure has started (CR 103.5).</summary>
public sealed record MulligansBegan : GameEvent
{
    public override string Rule => "103.5";

    public override string Describe() => "Opening hands dealt; mulligans begin.";
}

/// <summary>A player kept their opening hand (CR 103.5).</summary>
public sealed record MulliganKept(Guid PlayerId, int MulligansTaken) : GameEvent
{
    public override string Rule => "103.5";

    public override string Describe() =>
        $"{PlayerId:N} kept a hand after {MulligansTaken} mulligan(s).";
}

/// <summary>A player took a mulligan: hand shuffled back, new hand drawn (CR 103.5).</summary>
public sealed record MulliganTaken(Guid PlayerId, int MulligansTaken) : GameEvent
{
    public override string Rule => "103.5";

    public override string Describe() => $"{PlayerId:N} took a mulligan.";
}

/// <summary>The opening hands are settled and the first turn may begin (CR 103.5, 103.8).</summary>
public sealed record MulligansFinished : GameEvent
{
    public override string Rule => "103.5";

    public override string Describe() => "Opening hands are settled.";
}

/// <summary>
/// A player has taken whatever actions their opening hand allowed (CR 103.6).
/// </summary>
/// <remarks>
/// Emitted for every player, including one holding nothing that offers an action — the step is a
/// queue and this is what says the queue has moved on. Without it, a game rebuilt from its log
/// mid-step could not tell a player who declined from a player not yet asked, and would ask the
/// table again from the top.
/// <para>
/// The cards themselves arrive by the ordinary <see cref="ObjectMoved"/>, because starting the
/// game with a Leyline on the battlefield is a card changing zones like any other. This event
/// records the decision, not its result.
/// </para>
/// </remarks>
public sealed record OpeningHandActionsTaken(Guid PlayerId) : GameEvent
{
    public override string Rule => "103.6";

    public override string Describe() => $"{PlayerId:N} acted from their opening hand.";
}

// ---- Commander (CR 903) ---------------------------------------------------------------------

/// <summary>A player's commander was designated as the game began (CR 903.3, 903.6).</summary>
public sealed record CommanderDesignated(Guid PlayerId, string OracleId, ObjectId CardId) : GameEvent
{
    public override string Rule => "903.6";

    public override string Describe() => $"{PlayerId:N}'s commander is set.";
}

/// <summary>
/// A commander was cast from the command zone, so the next one costs {2} more (CR 903.8).
/// </summary>
public sealed record CommanderCastFromCommandZone(Guid PlayerId, int TimesCast) : GameEvent
{
    public override string Rule => "903.8";

    public override string Describe() =>
        $"{PlayerId:N} cast their commander from the command zone ({TimesCast} time(s)).";
}

/// <summary>
/// Combat damage from a commander, tracked over the whole game (CR 903.10a).
/// </summary>
public sealed record CommanderDamageDealt(
    Guid PlayerId, string CommanderOracleId, int Amount, int Total) : GameEvent
{
    public override string Rule => "903.10a";

    public override string Describe() =>
        $"{PlayerId:N} has taken {Total} damage from that commander.";
}

/// <summary>
/// Nothing happened, where something might have (CR 508.1b).
/// </summary>
/// <remarks>
/// A creature attacking a planeswalker that has left the battlefield assigns no combat damage —
/// it does not fall through to the player. Saying so as an event keeps the assignment code
/// total, and leaves a line in the log explaining why a hit landed nowhere.
/// </remarks>
public sealed record NothingHappened : GameEvent
{
    public override string Rule => "508.1b";

    public override string Describe() => "No damage was assigned.";
}
