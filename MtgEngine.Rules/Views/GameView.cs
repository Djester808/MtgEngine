using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Views;

/// <summary>Viewer-facing data contract. A privacy projector must be implemented before transport.</summary>
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
    GameOutcome? Outcome)
{
    public bool ManualLibraryActionsAllowed { get; init; }
    public bool ManualLibraryLookActive { get; init; }

    /// <summary>Most recent public reveal; historical details, never live hidden-zone objects.</summary>
    public CardReveal? LastReveal { get; init; }
    public CardReveal? LastLook { get; init; }
    public ImmutableList<ObjectView> InspectedLibraryCards { get; init; } = [];

    /// <summary>Board references for supported table sizes; existing zone payloads remain authoritative.</summary>
    public TableView? Table { get; init; }

    public CombatView? Combat { get; init; }

    /// <summary>Cards explicitly offered by the viewer's current library or opening-hand choice.</summary>
    public ImmutableList<ObjectView> ChoiceCards { get; init; } = [];
}

/// <summary>Player display data. These fields do not implement or evaluate rules.</summary>
[RuleRef("702.16j")]
public sealed record PlayerView(PlayerId Id, string Name, int Life, int HandCount, int LibraryCount, int GraveyardCount, ImmutableDictionary<string, int> Counters, ManaPool Mana, ImmutableList<ObjectView> Graveyard, ImmutableList<ObjectView> Command, bool ProtectionFromEverything = false)
{
    public bool HasLost { get; init; }

    public int LandsPlayedThisTurn { get; init; }
    /// <summary>
    /// CR 401.5: while an effect has this player playing with the top card of their library
    /// revealed, that card is revealed — to <em>every</em> player, which is the whole difference
    /// from "may look at the top card of your library", the private half of the same rule. It is
    /// null for every other seat, and a library is otherwise <see cref="LibraryCount"/> and
    /// nothing else, so no viewer learns a card this rule does not show them.
    /// <para>
    /// This projection uses the current object's id; it does not implement the reveal-history
    /// identity transitions required by CR 401.6. Those belong to authoritative state.
    /// It is a body property rather than a positional one so that no existing
    /// construction of a seat moves for a field most games never fill.
    /// </para>
    /// </summary>
    [RuleRef("401.5")]
    public ObjectView? RevealedTopCard { get; init; }

    /// <summary>The top card this viewer may privately inspect; never sent to other seats.</summary>
    public ObjectView? LookableTopCard { get; init; }
}

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
    /// any future rules evaluator must do.
    /// </summary>
    bool EveryCreatureType = false,

    /// <summary>
    /// CR 707: this object is a copy. A copy of a spell sits on the stack under the same name and
    /// with the same characteristics as the spell it copies (CR 707.10), so nothing else in the
    /// view tells the two apart, and a client that drew only the name would show one spell twice.
    /// It is public — the stack is a public zone (CR 400.2) — and it is what makes CR 704.5e
    /// legible at the table: this is the object that ceases to exist when it leaves.
    /// </summary>
    bool IsCopy = false)
{
    public string? PrintingId { get; init; }
    public CardFinish? Finish { get; init; }
    public string ArtworkKey => CardArtwork.Key(OracleId, PrintingId);

    public string? ManaCost { get; init; }

    /// <summary>Authoritative stack category; source card types do not classify abilities.</summary>
    public string? StackKind { get; init; }

    public string? OracleText { get; init; }

    public ImmutableList<string> Colors { get; init; } = [];

    public bool IsToken { get; init; }

    public bool SummoningSick { get; init; }

    public int TargetCount { get; init; }

    public ImmutableList<ActivatedAbilityView> Abilities { get; init; } = [];

    public ImmutableList<ObjectId> Targets { get; init; } = [];

    public ImmutableList<PlayerId> TargetPlayers { get; init; } = [];
}

public sealed record ActivatedAbilityView(int Index, string Text, bool RequiresTap, bool IsManaAbility, int TargetCount, bool AnnouncesVariable);

public sealed record CombatAttackView(ObjectId Attacker, PlayerId? DefendingPlayer, ObjectId? DefendingPermanent);

public sealed record CombatBlockView(ObjectId Attacker, ImmutableList<ObjectId> Blockers);

public sealed record CombatView(ImmutableList<CombatAttackView> Attackers, ImmutableList<CombatBlockView> Blocks, bool AttackersDeclared, bool BlockersDeclared)
{
    public ImmutableList<PlayerId> DefendingPlayers { get; init; } = [];

    public ImmutableList<PlayerId> BlockersDeclaredBy { get; init; } = [];
}

