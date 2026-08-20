using System.Collections.Immutable;
using MtgEngine.Domain.Models;
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
public sealed record ObjectMoved(
    ObjectId OldId,
    ObjectId NewId,
    Zone From,
    Zone To,
    Guid ControllerId,
    MoveCause Cause,
    ZonePosition Position = ZonePosition.Top) : GameEvent
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
public sealed record SpellCastEvent(Guid PlayerId, ObjectId StackId, string CardName) : GameEvent
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

/// <summary>Damage was marked on a permanent (CR 120.3).</summary>
/// <remarks>
/// Marking is not destroying. Damage sits on the permanent until state-based actions compare it
/// with toughness (CR 704.5g) or cleanup removes it (CR 514.2), which is what lets a creature
/// survive lethal damage if its toughness rises in between.
/// </remarks>
public sealed record DamageMarked(ObjectId Id, int Amount, bool FromDeathtouch = false) : GameEvent
{
    public override string Rule => "120.3";

    public override string Describe() => $"{Amount} damage marked on {Id}.";
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
    public override string Rule => "603.2";

    public override string Describe() => $"Triggered: {Text}";
}

/// <summary>
/// A waiting trigger went on the stack (CR 603.3), topmost, in APNAP order (CR 603.3b).
/// </summary>
public sealed record TriggerPutOnStack(
    ObjectId Id,
    ObjectId SourceId,
    CardDefinition SourceCard,
    string AbilityId,
    string Text,
    Guid ControllerId) : GameEvent
{
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
    ImmutableDictionary<ObjectId, Guid> Attackers) : GameEvent
{
    public override string Rule => "508.1";

    public override string Describe() => $"{Attackers.Count} creature(s) attack.";
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
