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
