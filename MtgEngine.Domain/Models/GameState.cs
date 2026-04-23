using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.ValueObjects;

namespace MtgEngine.Domain.Models;

/// <summary>
/// Immutable snapshot of a single player's state.
/// </summary>
public sealed record PlayerState
{
    public Guid PlayerId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public int Life { get; init; } = 20;
    public int PoisonCounters { get; init; } = 0;
    public ManaPool ManaPool { get; init; } = ManaPool.Empty;
    public ImmutableList<Card> Hand { get; init; } = ImmutableList<Card>.Empty;
    public ImmutableList<Card> Library { get; init; } = ImmutableList<Card>.Empty;
    public ImmutableList<Card> Graveyard { get; init; } = ImmutableList<Card>.Empty;
    public ImmutableList<Card> Exile { get; init; } = ImmutableList<Card>.Empty;
    public bool HasLandPlayedThisTurn { get; init; } = false;

    public bool HasLost =>
        Life <= 0 ||
        PoisonCounters >= 10 ||
        (Library.IsEmpty && /* attempted to draw */ false); // draw-loss handled in rules engine

    public PlayerState GainLife(int amount) => this with { Life = Life + amount };
    public PlayerState LoseLife(int amount) => this with { Life = Life - amount };
    public PlayerState AddPoison(int amount) => this with { PoisonCounters = PoisonCounters + amount };
    public PlayerState ClearManaPool() => this with { ManaPool = ManaPool.Empty };
    public PlayerState AddMana(ManaColor color, int count = 1) => this with { ManaPool = ManaPool.Add(color, count) };

    public PlayerState DrawCard()
    {
        if (Library.IsEmpty)
            throw new InvalidOperationException($"Player {Name} attempted to draw from an empty library.");
        var card = Library[0];
        return this with
        {
            Library = Library.RemoveAt(0),
            Hand = Hand.Add(card)
        };
    }

    public PlayerState AddCardToHand(Card card) => this with { Hand = Hand.Add(card) };
    public PlayerState RemoveCardFromHand(Guid cardId)
    {
        var card = Hand.FirstOrDefault(c => c.CardId == cardId)
            ?? throw new InvalidOperationException($"Card {cardId} not found in hand.");
        return this with { Hand = Hand.Remove(card) };
    }

    public PlayerState SendToGraveyard(Card card) => this with { Graveyard = Graveyard.Add(card) };
    public PlayerState SendToExile(Card card) => this with { Exile = Exile.Add(card) };
    public PlayerState ShuffleLibrary(Random? rng = null)
    {
        rng ??= Random.Shared;
        var shuffled = Library.ToList();
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        return this with { Library = shuffled.ToImmutableList() };
    }
}

/// <summary>
/// Immutable snapshot of the entire game state at a point in time.
/// Every game action produces a new GameState.
/// </summary>
public sealed record GameState
{
    public Guid GameId { get; init; } = Guid.NewGuid();
    public ImmutableList<PlayerState> Players { get; init; } = ImmutableList<PlayerState>.Empty;
    public ImmutableList<Permanent> Battlefield { get; init; } = ImmutableList<Permanent>.Empty;
    public ImmutableStack<IStackObject> Stack { get; init; } = ImmutableStack<IStackObject>.Empty;
    public int Turn { get; init; } = 1;
    public Guid ActivePlayerId { get; init; }
    public Guid PriorityPlayerId { get; init; }
    public Phase CurrentPhase { get; init; } = Phase.Beginning;
    public Step CurrentStep { get; init; } = Step.Untap;
    public GameResult Result { get; init; } = GameResult.InProgress;

    // Flags
    public bool StateBasedActionsRequired { get; init; } = false;
    public bool IsFirstTurn { get; init; } = true;

    // Combat state (null when not in combat)
    public CombatState? Combat { get; init; } = null;

    // Convenience
    public bool IsStackEmpty => Stack.IsEmpty;
    public bool IsInCombat => CurrentPhase == Phase.Combat;
    public bool GameOver => Result != GameResult.InProgress;

    public PlayerState GetPlayer(Guid playerId) =>
        Players.FirstOrDefault(p => p.PlayerId == playerId)
        ?? throw new InvalidOperationException($"Player {playerId} not found.");

    public PlayerState ActivePlayer => GetPlayer(ActivePlayerId);
    public PlayerState PriorityPlayer => GetPlayer(PriorityPlayerId);

    public Permanent GetPermanent(Guid permanentId) =>
        Battlefield.FirstOrDefault(p => p.PermanentId == permanentId)
        ?? throw new InvalidOperationException($"Permanent {permanentId} not found on battlefield.");

    public bool PermanentExists(Guid permanentId) =>
        Battlefield.Any(p => p.PermanentId == permanentId);

    public GameState UpdatePlayer(PlayerState updated) => this with
    {
        Players = Players.Replace(
            Players.First(p => p.PlayerId == updated.PlayerId),
            updated)
    };

    public GameState UpdatePermanent(Permanent updated) => this with
    {
        Battlefield = Battlefield.Replace(
            Battlefield.First(p => p.PermanentId == updated.PermanentId),
            updated)
    };

    public GameState RemovePermanent(Guid permanentId) => this with
    {
        Battlefield = Battlefield.RemoveAll(p => p.PermanentId == permanentId)
    };

    public GameState AddPermanent(Permanent permanent) => this with
    {
        Battlefield = Battlefield.Add(permanent)
    };

    public GameState PushStack(IStackObject obj) => this with
    {
        Stack = Stack.Push(obj)
    };

    public GameState PopStack(out IStackObject top)
    {
        var next = Stack.Pop(out top);
        return this with { Stack = next };
    }

    public Guid OpponentOf(Guid playerId) =>
        Players.First(p => p.PlayerId != playerId).PlayerId;

    public ImmutableList<Permanent> GetControlledPermanents(Guid controllerId) =>
        Battlefield.Where(p => p.ControllerId == controllerId).ToImmutableList();

    public ImmutableList<Permanent> GetCreatures() =>
        Battlefield.Where(p => p.IsCreature).ToImmutableList();

    public ImmutableList<Permanent> GetControlledCreatures(Guid controllerId) =>
        Battlefield.Where(p => p.IsCreature && p.ControllerId == controllerId).ToImmutableList();
}

/// <summary>
/// Tracks state during the combat phase.
/// </summary>
public sealed record CombatState
{
    /// <summary>PermanentId -> list of blocker PermanentIds</summary>
    public ImmutableDictionary<Guid, ImmutableList<Guid>> AttackersToBlockers { get; init; }
        = ImmutableDictionary<Guid, ImmutableList<Guid>>.Empty;

    /// <summary>Order in which attacker deals damage to multiple blockers.</summary>
    public ImmutableDictionary<Guid, ImmutableList<Guid>> BlockerOrder { get; init; }
        = ImmutableDictionary<Guid, ImmutableList<Guid>>.Empty;

    public bool AttackersDeclared { get; init; } = false;
    public bool BlockersDeclared { get; init; } = false;

    public IReadOnlyList<Guid> Attackers => AttackersToBlockers.Keys.ToList();

    public bool IsAttacking(Guid permanentId) => AttackersToBlockers.ContainsKey(permanentId);
    public bool IsBlocking(Guid permanentId) => AttackersToBlockers.Values.Any(list => list.Contains(permanentId));

    public ImmutableList<Guid> GetBlockers(Guid attackerId) =>
        AttackersToBlockers.TryGetValue(attackerId, out var list) ? list : ImmutableList<Guid>.Empty;
}
