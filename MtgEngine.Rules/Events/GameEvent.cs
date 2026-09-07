using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Events;

/// <summary>
/// Something that happened. The log is the game: state is a fold of events, and
/// <see cref="GameReducer.Replay"/> must equal the state it was folded from. Every event knows
/// how to apply itself, so a subsystem adds events in its own files and no shared reducer
/// grows. The serializer discovers event types by reflection and a snapshot test lists them.
/// </summary>
public abstract record GameEvent
{
    /// <summary>Fold this event into the state. Pure: no reads outside the state, no randomness that is not in the event.</summary>
    public abstract GameState Apply(GameState state);
}

/// <summary>Folds a log. The first event must be <see cref="GameStarted"/>.</summary>
public static class GameReducer
{
    public static GameState Replay(IEnumerable<GameEvent> log)
    {
        var state = GameStarted.Empty;
        foreach (var e in log)
            state = e.Apply(state);
        return state;
    }

    public static GameState Apply(GameState state, IEnumerable<GameEvent> events)
    {
        foreach (var e in events)
            state = e.Apply(state);
        return state;
    }
}

/// <summary>The first event: players, seed and format. Produces the initial state from nothing.</summary>
[RuleRef("103")]
public sealed record GameStarted(ImmutableList<PlayerStart> Players, ulong Seed, int StartingLife, string Format) : GameEvent
{
    internal static GameState Empty { get; } = new()
    {
        Players = [],
        Objects = ImmutableDictionary<ObjectId, GameObject>.Empty,
        Zones = ImmutableDictionary<ZoneKey, ImmutableList<ObjectId>>.Empty,
        Turn = new TurnState(0, new PlayerId(0), Step.Untap, 0),
    };

    public override GameState Apply(GameState state) => Empty with
    {
        Players = [.. Players.Select((p, i) => new PlayerState(new PlayerId(i), p.Name, StartingLife))],
        Rng = new RngState(Seed, 0),
        Turn = new TurnState(0, new PlayerId(0), Step.Untap, 0),
    };
}

public sealed record PlayerStart(string Name, ImmutableList<string> DeckOracleIds, ImmutableList<string> CommandZoneOracleIds);

/// <summary>An object came into existence in a zone, CR 400.7: a card put into a zone, a token created, a spell put on the stack.</summary>
[RuleRef("400.7")]
public sealed record ObjectCreated(GameObject Object, int Position) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        var zone = state.In(Object.Zone);
        var index = Position < 0 || Position > zone.Count ? zone.Count : Position;
        return state with
        {
            Objects = state.Objects.SetItem(Object.Id, Object),
            Zones = state.Zones.SetItem(Object.Zone, zone.Insert(index, Object.Id)),
            NextObjectId = Math.Max(state.NextObjectId, Object.Id.Value + 1),
            NextTimestamp = Math.Max(state.NextTimestamp, Object.Timestamp + 1),
        };
    }
}

/// <summary>An object left the game entirely: a token in a zone other than the battlefield, a resolved spell's copy, an exiled object with no memory.</summary>
[RuleRef("400.7")]
public sealed record ObjectCeasedToExist(ObjectId Id) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        if (!state.Objects.TryGetValue(Id, out var obj))
            return state;
        return state with
        {
            Objects = state.Objects.Remove(Id),
            Zones = state.Zones.SetItem(obj.Zone, state.In(obj.Zone).Remove(Id)),
        };
    }
}

/// <summary>
/// An object changed zones. It becomes a new object with a new id, CR 400.7; the event carries
/// both so replay and views can follow it. <paramref name="Position"/> is the index in the
/// destination, or -1 for the end.
/// </summary>
[RuleRef("400.7")]
public sealed record ObjectMoved(ObjectId From, GameObject To, int Position) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        var withoutOld = new ObjectCeasedToExist(From).Apply(state);
        return new ObjectCreated(To, Position).Apply(withoutOld);
    }
}

/// <summary>A stored property of an object changed: tapped, face down, phased out, summoning sickness, damage, attachment.</summary>
public sealed record ObjectChanged(ObjectId Id, bool? Tapped = null, bool? FaceDown = null, bool? PhasedOut = null, bool? SummoningSick = null, int? Damage = null, ObjectId? AttachedTo = null, bool ClearAttachment = false) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        if (!state.Objects.TryGetValue(Id, out var obj))
            return state;
        var updated = obj with
        {
            Tapped = Tapped ?? obj.Tapped,
            FaceDown = FaceDown ?? obj.FaceDown,
            PhasedOut = PhasedOut ?? obj.PhasedOut,
            SummoningSick = SummoningSick ?? obj.SummoningSick,
            Damage = Damage ?? obj.Damage,
            AttachedTo = ClearAttachment ? null : (AttachedTo ?? obj.AttachedTo),
        };
        return state with { Objects = state.Objects.SetItem(Id, updated) };
    }
}

[RuleRef("122")]
public sealed record CountersChanged(ObjectId Id, string Kind, int Delta) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        if (!state.Objects.TryGetValue(Id, out var obj))
            return state;
        var current = obj.Counters.GetValueOrDefault(Kind);
        var next = Math.Max(0, current + Delta);
        var counters = next == 0 ? obj.Counters.Remove(Kind) : obj.Counters.SetItem(Kind, next);
        return state with { Objects = state.Objects.SetItem(Id, obj with { Counters = counters }) };
    }
}

[RuleRef("119")]
public sealed record LifeChanged(PlayerId Player, int Delta, string Reason) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        var p = state.Player(Player);
        return state with { Players = state.Players.SetItem(Player.Value, p with { Life = p.Life + Delta }) };
    }
}

[RuleRef("122.1")]
public sealed record PlayerCountersChanged(PlayerId Player, string Kind, int Delta) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        var p = state.Player(Player);
        var next = Math.Max(0, p.Counters.GetValueOrDefault(Kind) + Delta);
        var counters = next == 0 ? p.Counters.Remove(Kind) : p.Counters.SetItem(Kind, next);
        return state with { Players = state.Players.SetItem(Player.Value, p with { Counters = counters }) };
    }
}

[RuleRef("500")]
public sealed record TurnBegan(int Number, PlayerId ActivePlayer, bool Extra) : GameEvent
{
    public override GameState Apply(GameState state) => state with
    {
        Turn = state.Turn with { Number = Number, ActivePlayer = ActivePlayer, Step = Step.Untap, IsExtraTurn = Extra },
        Players = [.. state.Players.Select(p => p with { LandsPlayedThisTurn = 0, ObjectsDrawnThisTurn = 0 })],
    };
}

[RuleRef("500.1")]
public sealed record StepBegan(Step Step) : GameEvent
{
    public override GameState Apply(GameState state) => state with { Turn = state.Turn with { Step = Step } };
}

[RuleRef("117")]
public sealed record PriorityGranted(PlayerId Player) : GameEvent
{
    public override GameState Apply(GameState state) => state with { Priority = new PriorityState(Player, []) };
}

[RuleRef("117.4")]
public sealed record PriorityPassed(PlayerId Player) : GameEvent
{
    public override GameState Apply(GameState state) => state with
    {
        Priority = state.Priority with { Holder = null, PassedInSuccession = state.Priority.PassedInSuccession.Add(Player) },
    };
}

/// <summary>The game halted to ask. Nothing else happens until <see cref="ChoiceAnswered"/>.</summary>
public sealed record ChoiceRequested(PendingChoice Choice) : GameEvent
{
    public override GameState Apply(GameState state) => state with
    {
        Pending = Choice,
        NextChoiceId = Math.Max(state.NextChoiceId, Choice.Id.Value + 1),
    };
}

public sealed record ChoiceAnswered(ChoiceId Id, PlayerId Player, ImmutableList<string> Answer) : GameEvent
{
    public override GameState Apply(GameState state) =>
        state.Pending?.Id == Id ? state with { Pending = null } : state;
}

/// <summary>A zone's order changed without any object changing zones: a shuffle, or cards put back in a chosen order.</summary>
[RuleRef("701.24")]
public sealed record ZoneReordered(ZoneKey Zone, ImmutableList<ObjectId> Order) : GameEvent
{
    public override GameState Apply(GameState state) => state with { Zones = state.Zones.SetItem(Zone, Order) };
}

/// <summary>A player drew when the library was empty, CR 121.4. The state-based action in 704.5b does the rest.</summary>
[RuleRef("121.4")]
public sealed record DrawFromEmptyLibraryAttempted(PlayerId Player) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        var p = state.Player(Player);
        return state with { Players = state.Players.SetItem(Player.Value, p with { DrewFromEmptyLibrary = true }) };
    }
}

/// <summary>Damage was dealt, CR 120. Marked on a permanent, or taken from a life total; the source's characteristics decide lifelink, infect and the rest, and those are separate events.</summary>
[RuleRef("120")]
public sealed record DamageDealt(ObjectId Source, ObjectId? ToObject, PlayerId? ToPlayer, int Amount, bool Combat, bool FromCommander) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        if (ToObject is { } id && state.Objects.TryGetValue(id, out var obj))
            return state with { Objects = state.Objects.SetItem(id, obj with { Damage = obj.Damage + Amount }) };
        if (ToPlayer is { } pid)
        {
            var p = state.Player(pid);
            var commanderDamage = FromCommander ? p.CommanderDamage.SetItem(Source, p.CommanderDamage.GetValueOrDefault(Source) + Amount) : p.CommanderDamage;
            return state with { Players = state.Players.SetItem(pid.Value, p with { Life = p.Life - Amount, CommanderDamage = commanderDamage }) };
        }

        return state;
    }
}

/// <summary>Mana entered a pool, CR 106.4. Positive amounts add; the mana subsystem spends with negative ones.</summary>
[RuleRef("106.4")]
public sealed record ManaChanged(PlayerId Player, ManaPool Delta, string Reason) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        var p = state.Player(Player);
        var m = p.Mana;
        var next = new ManaPool(m.White + Delta.White, m.Blue + Delta.Blue, m.Black + Delta.Black, m.Red + Delta.Red, m.Green + Delta.Green, m.Colorless + Delta.Colorless);
        return state with { Players = state.Players.SetItem(Player.Value, p with { Mana = next }) };
    }
}

[RuleRef("106.4")]
public sealed record ManaPoolEmptied(PlayerId Player) : GameEvent
{
    public override GameState Apply(GameState state) =>
        state with { Players = state.Players.SetItem(Player.Value, state.Player(Player) with { Mana = ManaPool.Empty }) };
}

/// <summary>A spell was cast, CR 601: the object is already on the stack by an <see cref="ObjectMoved"/>; this names the moment for triggers.</summary>
[RuleRef("601.2i")]
public sealed record SpellCast(ObjectId Spell, PlayerId Caster, Zone From) : GameEvent
{
    public override GameState Apply(GameState state) => state;
}

[RuleRef("602.2")]
public sealed record AbilityActivated(ObjectId StackObject, ObjectId Source, int AbilityIndex, PlayerId Player) : GameEvent
{
    public override GameState Apply(GameState state) => state;
}

[RuleRef("305")]
public sealed record LandPlayed(ObjectId Land, PlayerId Player) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        var p = state.Player(Player);
        return state with { Players = state.Players.SetItem(Player.Value, p with { LandsPlayedThisTurn = p.LandsPlayedThisTurn + 1 }) };
    }
}

[RuleRef("608.2")]
public sealed record StackObjectResolved(ObjectId StackObject) : GameEvent
{
    public override GameState Apply(GameState state) => state;
}

[RuleRef("508")]
public sealed record AttackersDeclared(PlayerId Player, ImmutableDictionary<ObjectId, CombatTarget> Attackers) : GameEvent
{
    public override GameState Apply(GameState state) => state with
    {
        Combat = new CombatState(Player, Attackers, ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty, AttackersDeclared: true, BlockersDeclared: false),
    };
}

[RuleRef("509")]
public sealed record BlockersDeclared(PlayerId Player, ImmutableDictionary<ObjectId, ImmutableList<ObjectId>> Blockers) : GameEvent
{
    public override GameState Apply(GameState state) =>
        state.Combat is null ? state : state with { Combat = state.Combat with { Blockers = Blockers, BlockersDeclared = true } };
}

[RuleRef("511")]
public sealed record CombatEnded : GameEvent
{
    public override GameState Apply(GameState state) => state with { Combat = null };
}

/// <summary>An object was destroyed, sacrificed, countered or otherwise left by a named rule; the zone change itself is a separate <see cref="ObjectMoved"/>.</summary>
public sealed record ObjectLeftBy(ObjectId Object, string Verb, ObjectId? By) : GameEvent
{
    public override GameState Apply(GameState state) => state;
}

/// <summary>The randomness was used: the state's counter advanced. The consuming event records what came out.</summary>
public sealed record RngAdvanced(RngState To) : GameEvent
{
    public override GameState Apply(GameState state) => state with { Rng = To };
}

[RuleRef("104.3")]
public sealed record PlayerLost(PlayerId Player, string Reason) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        var p = state.Player(Player);
        return state with { Players = state.Players.SetItem(Player.Value, p with { HasLost = true }) };
    }
}

[RuleRef("104")]
public sealed record GameEnded(GameOutcome Outcome) : GameEvent
{
    public override GameState Apply(GameState state) => state with { Outcome = Outcome, Priority = PriorityState.None };
}

/// <summary>
/// A player did something by hand on an honour-system object. The actor is recorded so the
/// log shows who did what; the engine applied no rule to it.
/// </summary>
public sealed record ManualActionTaken(PlayerId Actor, string Action, ObjectId? Object, string Detail) : GameEvent
{
    public override GameState Apply(GameState state) => state;
}
