using MtgEngine.Cards;

namespace MtgEngine.Rules.State;

/// <summary>Immutable game snapshot. Data and structural checks only, with no rules executor.</summary>
public sealed record GameState
{
    public required ImmutableList<PlayerState> Players { get; init; }
    public required ImmutableDictionary<ObjectId, GameObject> Objects { get; init; }
    /// <summary>Libraries and graveyards are top-first; the stack is bottom-first.</summary>
    public required ImmutableDictionary<ZoneKey, ImmutableList<ObjectId>> Zones { get; init; }
    public required TurnState Turn { get; init; }
    /// <summary>Server-only allocation high-water marks, retained when objects leave zones.</summary>
    public int LastObjectId { get; init; }
    public int LastTimestamp { get; init; }
    /// <summary>Explicit permission to inspect a current face-down exiled object. Never follows a card to a new object.</summary>
    public ImmutableDictionary<ObjectId, ImmutableHashSet<PlayerId>> ExileLookPermissions { get; init; } =
        ImmutableDictionary<ObjectId, ImmutableHashSet<PlayerId>>.Empty;
    public PriorityState Priority { get; init; } = PriorityState.None;
    public PendingChoice? Pending { get; init; }
    public GameOutcome? Outcome { get; init; }
    public CombatState? Combat { get; init; }
    public OpeningHandsState? OpeningHands { get; init; }
    public string Format { get; init; } = "commander";
    public CardReveal? LastReveal { get; init; }
    public ImmutableDictionary<PlayerId, CardReveal> LastLooks { get; init; } = ImmutableDictionary<PlayerId, CardReveal>.Empty;
    public ImmutableDictionary<string, LibraryInspection> LibraryInspections { get; init; } = ImmutableDictionary<string, LibraryInspection>.Empty;

    public PlayerState Player(PlayerId id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id.Value);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id.Value, Players.Count);
        var player = Players[id.Value];
        if (player.Id != id)
            throw new InvalidOperationException("The stored player identity does not match its seat.");
        return player;
    }

    public ImmutableList<ObjectId> In(ZoneKey zone) => Zones.TryGetValue(zone, out var list) ? list : [];

    /// <summary>Checks seats, zone topology and unique membership. Does not start or advance a game.</summary>
    public void Validate()
    {
        if (Players.Count is < 2 or > 4)
            throw new InvalidOperationException("A table requires two to four players.");
        for (var index = 0; index < Players.Count; index++)
            _ = Player(new PlayerId(index));
        var seen = new HashSet<ObjectId>();
        var physicalCards = new HashSet<CardInstanceId>();
        if (LastObjectId < 0 || LastTimestamp < 0)
            throw new InvalidOperationException("Allocation high-water marks cannot be negative.");
        foreach (var (zone, members) in Zones)
        {
            if (!Enum.IsDefined(zone.Zone) || zone.Zone == Zone.OutsideTheGame)
                throw new InvalidOperationException("Unknown zone.");
            var personal = zone.Zone is Zone.Library or Zone.Hand or Zone.Graveyard;
            if (personal != zone.Player.HasValue)
                throw new InvalidOperationException("Zone ownership does not match its topology.");
            if (zone.Player is { } owner)
                _ = Player(owner);
            foreach (var id in members)
            {
                if (!seen.Add(id) || !Objects.TryGetValue(id, out var obj) || obj.Id != id || obj.Zone != zone)
                    throw new InvalidOperationException("Object membership is inconsistent.");
                _ = Player(obj.Owner);
                _ = Player(obj.Controller);
                _ = Player(obj.InitialController);
                if (id.Value <= 0 || obj.Timestamp <= 0 || (personal && obj.Owner != zone.Player))
                    throw new InvalidOperationException("Object identity, timestamp or personal-zone owner is invalid.");
                obj.Definition.Validate();
                _ = obj.Face;
                if (obj.CardInstanceId is { } physical
                    && (physical.Value <= 0 || !physicalCards.Add(physical) || obj.IsToken || obj.IsCopy))
                    throw new InvalidOperationException("Physical card identities must be positive, unique and belong to cards.");
                if (obj.DeckSelection is { } selection)
                {
                    selection.Validate();
                    if (obj.CardInstanceId is null || !string.Equals(selection.OracleId, obj.Definition.OracleId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Deck selection must match a physical card.");
                }
            }
        }
        if (seen.Count != Objects.Count)
            throw new InvalidOperationException("Every object must belong to exactly one zone.");
        foreach (var (id, viewers) in ExileLookPermissions)
        {
            if (!Objects.TryGetValue(id, out var obj) || obj.Zone.Zone != Zone.Exile || !obj.FaceDown)
                throw new InvalidOperationException("Exile inspection must name a current face-down exiled object.");
            foreach (var viewer in viewers)
                _ = Player(viewer);
        }
        _ = new Turn.TurnSnapshot(this);
        _ = new Turn.PrioritySnapshot(this);
    }
}

[RuleRef("500")]
public sealed record TurnState(int Number, PlayerId ActivePlayer, Step Step, int TurnOrderIndex)
{
    /// <summary>Extra turns waiting to be taken, most recent first, CR 500.7.</summary>
    public ImmutableList<PlayerId> ExtraTurns { get; init; } = [];

    public bool IsExtraTurn { get; init; }
}

[RuleRef("117")]
public sealed record PriorityState(PlayerId? Holder, ImmutableList<PlayerId> PassedInSuccession)
{
    public static PriorityState None { get; } = new(null, []);
}

[RuleRef("506")]
public sealed record CombatState(
    PlayerId AttackingPlayer,
    ImmutableDictionary<ObjectId, CombatTarget> Attackers,
    ImmutableDictionary<ObjectId, ImmutableList<ObjectId>> Blockers,
    bool AttackersDeclared,
    bool BlockersDeclared)
{
    public ImmutableList<PlayerId> DefendingPlayers { get; init; } = [];

    public ImmutableList<PlayerId> BlockersDeclaredBy { get; init; } = [];
}

/// <summary>What an attacker attacks: a player, or a planeswalker or battle, CR 506.1.</summary>
public sealed record CombatTarget(PlayerId? Player, ObjectId? Permanent);

/// <summary>Choice description only; no execution continuation is stored.</summary>
public sealed record PendingChoice(ChoiceId Id, PlayerId Player, string Kind, string Prompt,
    ImmutableList<ChoiceOption> Options, int Min, int Max);
public sealed record ChoiceOption(string Id, string Label, ObjectId? Object = null, PlayerId? Player = null);
public sealed record GameOutcome(ImmutableList<PlayerId> Winners, bool Draw, string Reason);
