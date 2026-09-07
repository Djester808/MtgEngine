using MtgEngine.Cards;

namespace MtgEngine.Rules.State;

/// <summary>
/// The whole game at one instant. Immutable: every event produces a new state, and
/// <c>GameReducer.Replay(log)</c> must equal the state it was folded from. Nothing here is
/// derived; anything that can be computed from the log and the definitions is computed.
/// </summary>
public sealed record GameState
{
    public required ImmutableList<PlayerState> Players { get; init; }

    public required ImmutableDictionary<ObjectId, GameObject> Objects { get; init; }

    /// <summary>Ordered contents of every zone. Libraries are top-first; the stack is bottom-first.</summary>
    public required ImmutableDictionary<ZoneKey, ImmutableList<ObjectId>> Zones { get; init; }

    public required TurnState Turn { get; init; }

    public PriorityState Priority { get; init; } = PriorityState.None;

    public CombatState? Combat { get; init; }

    public PendingChoice? Pending { get; init; }

    public ImmutableList<ActiveEffect> ActiveEffects { get; init; } = [];

    public ImmutableList<DelayedTriggerState> DelayedTriggers { get; init; } = [];

    public RngState Rng { get; init; } = new(1, 0);

    public int NextObjectId { get; init; } = 1;

    public int NextTimestamp { get; init; } = 1;

    public int NextChoiceId { get; init; } = 1;

    public GameOutcome? Outcome { get; init; }

    public PlayerState Player(PlayerId id) => Players[id.Value];

    public ImmutableList<ObjectId> In(ZoneKey zone) => Zones.TryGetValue(zone, out var list) ? list : [];
}

[RuleRef("500")]
public sealed record TurnState(int Number, PlayerId ActivePlayer, Step Step, int TurnOrderIndex)
{
    /// <summary>Extra turns waiting to be taken, most recent first, CR 500.7.</summary>
    public ImmutableList<PlayerId> ExtraTurns { get; init; } = [];

    public ImmutableList<Step> ExtraSteps { get; init; } = [];

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
    bool BlockersDeclared);

/// <summary>What an attacker attacks: a player, or a planeswalker or battle, CR 506.1.</summary>
public sealed record CombatTarget(PlayerId? Player, ObjectId? Permanent);

/// <summary>A continuous effect in force, with what created it, when, and until when, CR 611.</summary>
[RuleRef("611")]
public sealed record ActiveEffect(
    int Id,
    ContinuousEffect Effect,
    ObjectId? Source,
    PlayerId Controller,
    ImmutableList<ObjectId> AppliesTo,
    ImmutableList<PlayerId> AppliesToPlayers,
    Duration Until,
    int Timestamp,
    bool FromStaticAbility);

[RuleRef("603.7")]
public sealed record DelayedTriggerState(int Id, Trigger When, Effect Do, ObjectId? Source, PlayerId Controller, bool Once, ImmutableDictionary<string, ImmutableList<ObjectId>> Remembered);

/// <summary>The game stops and asks. Nothing else happens until this is answered by an event.</summary>
public sealed record PendingChoice(
    ChoiceId Id,
    PlayerId Player,
    string Kind,
    string Prompt,
    ImmutableList<ChoiceOption> Options,
    int Min,
    int Max,
    ChoiceContext Context);

public sealed record ChoiceOption(string Id, string Label, ObjectId? Object = null, PlayerId? Player = null);

/// <summary>A locator for where the answer goes, so resumption is a branch per kind and never a captured continuation.</summary>
public sealed record ChoiceContext(ObjectId? Source, int? AbilityIndex, string? EffectPath, ImmutableDictionary<string, string> Extra);

/// <summary>A deterministic generator. The seed is in the log; the counter advances with every draw.</summary>
public readonly record struct RngState(ulong Seed, int Counter);

public sealed record GameOutcome(ImmutableList<PlayerId> Winners, bool Draw, string Reason);
