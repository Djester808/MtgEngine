using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Views;

/// <summary>
/// What one player is allowed to see. Built by the projector from the state; never the state
/// itself. Libraries, other hands and face-down cards are absent, not flagged. Wave 2 fills
/// this in; the shape here is the seam.
/// </summary>
public sealed record GameView(
    PlayerId Viewer,
    int TurnNumber,
    Step Step,
    PlayerId ActivePlayer,
    PlayerId? PriorityHolder,
    ImmutableList<PlayerView> Players,
    ImmutableList<ObjectView> Battlefield,
    ImmutableList<ObjectView> Stack,
    ImmutableList<ObjectView> Hand,
    ImmutableList<ObjectView> Exile,
    PendingChoice? Pending,
    GameOutcome? Outcome);

public sealed record PlayerView(PlayerId Id, string Name, int Life, int HandCount, int LibraryCount, int GraveyardCount, ImmutableDictionary<string, int> Counters, ManaPool Mana, ImmutableList<ObjectView> Graveyard, ImmutableList<ObjectView> Command);

public sealed record ObjectView(
    ObjectId Id,
    string Name,
    string OracleId,
    PlayerId Controller,
    PlayerId Owner,
    bool Tapped,
    bool FaceDown,
    int? Power,
    int? Toughness,
    int? Loyalty,
    int Damage,
    ImmutableDictionary<string, int> Counters,
    ObjectId? AttachedTo,
    ImmutableList<string> Keywords,
    ImmutableList<string> Types,
    ImmutableList<string> Subtypes,
    bool Enforced,

    /// <summary>
    /// CR 205.3d: this object is every creature type, which is what changeling grants
    /// (CR 702.72a). The names are not in <see cref="Subtypes"/> — CR 205.3m's list changes with
    /// every set — so a client that asks "is this a Goblin" reads this flag beside the list, as
    /// the engine's own readers do.
    /// </summary>
    bool EveryCreatureType = false);

/// <summary>What a player may do now. The client enables controls from this and reasons about nothing.</summary>
public sealed record LegalActions(
    ImmutableList<ObjectId> Castable,
    ImmutableList<ActivatableAbility> Activatable,
    ImmutableList<ObjectId> PlayableLands,
    ImmutableList<ObjectId> EligibleAttackers,
    ImmutableList<ObjectId> EligibleBlockers,
    bool MayPass,
    bool MustAnswerChoice);

public sealed record ActivatableAbility(ObjectId Source, int AbilityIndex, string Text);
