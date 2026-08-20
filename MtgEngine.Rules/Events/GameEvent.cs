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
/// A library was shuffled, and this is the order it came out in (CR 103.2, 701.20).
/// </summary>
/// <remarks>
/// The resulting order is recorded, not the seed. A seed only reproduces the shuffle if the
/// shuffling algorithm never changes; the order reproduces it forever.
/// </remarks>
public sealed record LibraryShuffled(Guid PlayerId, ImmutableList<ObjectId> Order) : GameEvent
{
    public override string Rule => "701.20";

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
