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

/// <summary>
/// What is known about one player, CR 400.2. Every field but the last is state read straight off
/// the seat; <c>ProtectionFromEverything</c> is CR 702.16j computed through the layers, and it is
/// here because it is public and because nothing else in the view explains why damage to that
/// player never lands — a client draws it beside the life total, the way a designation is drawn.
/// The other player limits of CR 613.10 are not here: this is the one a table can see the effect
/// of on every damage event, and a field the view does not need is a field it does not carry.
/// </summary>
[RuleRef("702.16j")]
public sealed record PlayerView(PlayerId Id, string Name, int Life, int HandCount, int LibraryCount, int GraveyardCount, ImmutableDictionary<string, int> Counters, ManaPool Mana, ImmutableList<ObjectView> Graveyard, ImmutableList<ObjectView> Command, bool ProtectionFromEverything = false);

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

    /// <summary>
    /// CR 303.4f: an Aura is attached to an object <em>or a player</em>, and the second half is
    /// this. It travels beside <see cref="AttachedTo"/> for every viewer because an attachment is
    /// public information (CR 400.2) — the table can see what the Aura is on, and a client that
    /// read only the object half would draw an "enchant player" Aura floating on nothing.
    /// </summary>
    PlayerId? AttachedToPlayer,

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
