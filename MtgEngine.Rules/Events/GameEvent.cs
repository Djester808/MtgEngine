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
public sealed record GameStarted(ImmutableList<PlayerStart> Players, ulong Seed, int StartingLife, string Format, int StartingPlayer = 0) : GameEvent
{
    /// <summary>The table's admitted automated coverage. Null identifies older logs without this snapshot.</summary>
    public ImmutableHashSet<string>? EnforcedOracleIds { get; init; }

    internal static GameState Empty { get; } = new()
    {
        Players = [],
        Objects = ImmutableDictionary<ObjectId, GameObject>.Empty,
        Zones = ImmutableDictionary<ZoneKey, ImmutableList<ObjectId>>.Empty,
        Turn = new TurnState(0, new PlayerId(0), Step.Untap, 0),
    };

    /// <summary>CR 103.1: the starting player is decided before anything else; the turn system reads it from the state.</summary>
    public override GameState Apply(GameState state) => Empty with
    {
        Players = [.. Players.Select((p, i) => new PlayerState(new PlayerId(i), p.Name, StartingLife))],
        Rng = new RngState(Seed, 0),
        Turn = new TurnState(0, new PlayerId(StartingPlayer), Step.Untap, StartingPlayer),
        Format = Format,
    };
}

/// <summary>A card was drawn, CR 121.1: the move itself is the preceding <see cref="ObjectMoved"/>; this names the draw for triggers and replacements.</summary>
[RuleRef("121.1")]
public sealed record ObjectDrawn(PlayerId Player, ObjectId Object) : GameEvent
{
    /// <summary>Only replacement proposals carry this flag; committed draws follow their movement event.</summary>
    public bool AwaitingCardMovement { get; init; }

    public override GameState Apply(GameState state)
    {
        var p = state.Player(Player);
        // CR 121.1: both counters, the turn's and the step's (E2c ask 1).
        return state with { Players = state.Players.SetItem(Player.Value, p.Drew()) };
    }
}

/// <summary>A per-player flag was consumed: "drewFromEmptyLibrary" once the loss for it is applied, CR 704.5b.</summary>
public sealed record PlayerFlagCleared(PlayerId Player, string Flag) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        var p = state.Player(Player);
        var next = Flag switch
        {
            "drewFromEmptyLibrary" => p with { DrewFromEmptyLibrary = false },
            _ => p,
        };
        return state with { Players = state.Players.SetItem(Player.Value, next) };
    }
}

public sealed record PlayerStart(string Name, ImmutableList<StartingCard> DeckCards, ImmutableList<StartingCard> CommandCards);

/// <summary>An object came into existence in a zone, CR 400.7: a card put into a zone, a token created, a spell put on the stack.</summary>
[RuleRef("400.7")]
public sealed record ObjectCreated(GameObject Object, int Position) : GameEvent
{
    public override GameState Apply(GameState state) => Place(state, Object, Position, created: true);

    /// <summary>
    /// Puts an object into its zone. <paramref name="created"/> says whether this is the object
    /// coming into existence, which is the only case that writes creation history: ObjectMoved
    /// places the new object through here too, and a move is not a creation.
    /// </summary>
    internal static GameState Place(GameState state, GameObject Object, int Position, bool created)
    {
        var zone = state.In(Object.Zone);
        var index = Position < 0 || Position > zone.Count ? zone.Count : Position;
        var history = state.History;
        if (created && Object.IsToken)
        {
            // CR 111.1: the turn remembers the token as it was created, under the player who created it.
            var mine = history.TokensCreated.TryGetValue(Object.InitialController, out var made) ? made : [];
            history = history with { TokensCreated = history.TokensCreated.SetItem(Object.InitialController, mine.Add(Object)) };

            // CR 111.1: a token created on the battlefield has entered it (E2c ask 3). A card a test or
            // a scenario places on the battlefield directly stands for one that was already there.
            if (Object.Zone.Zone == Zone.Battlefield)
                history = history with { EnteredBattlefield = history.EnteredBattlefield.Add(Object.Id) };
        }

        return state with
        {
            Objects = state.Objects.SetItem(Object.Id, Object),
            Zones = state.Zones.SetItem(Object.Zone, zone.Insert(index, Object.Id)),
            NextObjectId = Math.Max(state.NextObjectId, Object.Id.Value + 1),
            NextTimestamp = Math.Max(state.NextTimestamp, Object.Timestamp + 1),
            History = history,
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
            CommanderReturnCandidates = state.CommanderReturnCandidates.Remove(Id),
            Zones = state.Zones.SetItem(obj.Zone, state.In(obj.Zone).Remove(Id)),
        };
    }
}

/// <summary>
/// An object changed zones, CR 400.7: the old object ceases and <paramref name="To"/> is the new one.
/// <paramref name="Departed"/> is the old object as it last existed, for the abilities that look back
/// at it (CR 603.10) and for a trigger on "from" a zone that is not the battlefield.
/// <paramref name="Position"/> is the index in the destination, or -1 for the end.
/// <paramref name="FromZone"/> is the zone it left, for a mover that knows the zone but carries no
/// departed object (CR 603.6); a move that carries one already says it, so it is defaulted and no
/// existing site changes.
/// </summary>
[RuleRef("400.7")]
public sealed record ObjectMoved(ObjectId From, GameObject To, int Position, GameObject? Departed = null, ZoneKey? FromZone = null) : GameEvent
{
    public Library.LibraryDepartureObservation? LibraryDeparture { get; init; }
    /// <summary>
    /// The zone the object left, CR 603.6, or none when the site that made the move did not say.
    /// <see cref="FromZone"/> is what the mover recorded; a move that carries <see cref="Departed"/>
    /// says it by that object's own zone, which is why the zone system's moves answer already. A
    /// trigger that names a source zone is refused rather than guessed when this is none, because a
    /// guessed source fires abilities that should not fire (CR 603.6).
    /// </summary>
    [RuleRef("603.6")]
    [System.Text.Json.Serialization.JsonIgnore]
    public ZoneKey? LeftFrom => FromZone ?? Departed?.Zone;

    public override GameState Apply(GameState state)
    {
        // Where it came from: the state still holds it at this point, and failing that the mover
        // said so. FromZone was read by LeftFrom but NOT here, so a mover that recorded the zone
        // and carried no departed object - the very case the parameter exists for - would have
        // written no LeftBattlefield and no Died, and morbid and revolt would have missed it.
        var known = state.Objects.TryGetValue(From, out var old);
        var fromZone = known && old is not null ? old.Zone.Zone : FromZone?.Zone;
        var withoutOld = new ObjectCeasedToExist(From).Apply(state);

        // created: false - this object is not coming into existence, it is arriving. Placing it
        // as a creation recorded a moved token in TokensCreated ("you created a token this turn"
        // answered true for a token that merely died) and added its battlefield entry twice.
        var moved = ObjectCreated.Place(withoutOld, To, Position, created: false);

        // The turn's history, for "entered this turn", morbid and revolt (CR 603.10 looks back at these).
        var h = moved.History;
        if (To.Zone.Zone == Zone.Battlefield)
            h = h with { EnteredBattlefield = h.EnteredBattlefield.Add(To.Id) };
        // CR 603.10: the object as it last existed is what looks back at it — morbid asks whether a
        // creature died, revolt who controlled what left — so the departed object itself is kept.
        if (fromZone == Zone.Battlefield && (Departed ?? old) is { } departed)
        {
            h = h with { LeftBattlefield = h.LeftBattlefield.Add(departed) };
            if (To.Zone.Zone == Zone.Graveyard)
                h = h with { Died = h.Died.Add(departed) };
        }

        var candidates = moved.CommanderReturnCandidates.Remove(From);
        if (To.IsCommander && To.Zone.Zone is Zone.Graveyard or Zone.Exile)
            candidates = candidates.Add(To.Id);
        return moved with { History = h, CommanderReturnCandidates = candidates };
    }
}

/// <summary>A stored property of an object changed: tapped, face down, phased out, summoning sickness, damage, attachment. An attachment to an object and one to a player (CR 303.4f) exclude each other; <paramref name="ClearAttachment"/> clears both.</summary>
public sealed record ObjectChanged(ObjectId Id, bool? Tapped = null, bool? FaceDown = null, bool? PhasedOut = null, bool? SummoningSick = null, int? Damage = null, ObjectId? AttachedTo = null, bool ClearAttachment = false, bool? DealtDeathtouchDamage = null, PlayerId? AttachedToPlayer = null) : GameEvent
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
            AttachedTo = ClearAttachment || AttachedToPlayer is not null ? null : (AttachedTo ?? obj.AttachedTo),
            AttachedToPlayer = ClearAttachment || AttachedTo is not null ? null : (AttachedToPlayer ?? obj.AttachedToPlayer),
            DealtDeathtouchDamage = DealtDeathtouchDamage ?? obj.DealtDeathtouchDamage,
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
        return state with { Objects = state.Objects.SetItem(Id, ApplyTo(obj)) };
    }

    internal GameObject ApplyTo(GameObject obj)
    {
        var current = obj.Counters.GetValueOrDefault(Kind);
        var next = Math.Max(0, current + Delta);
        var counters = next == 0 ? obj.Counters.Remove(Kind) : obj.Counters.SetItem(Kind, next);
        return obj with { Counters = counters };
    }
}

[RuleRef("119")]
/// <summary>CR 725.1: a player becomes the monarch; there is only ever one, so the designation moves to them.</summary>
[RuleRef("725.1")]
public sealed record MonarchChanged(PlayerId Player) : GameEvent
{
    public override GameState Apply(GameState state) => state with { Monarch = Player };
}

/// <summary>
/// A player took the initiative, CR 726.2: a designation the game keeps rather than one an
/// object carries, so it needs an event of its own or a replay could not restore it.
/// <para>
/// GameState.Initiative had no writer at all, while Query.Conditions read it - so every card
/// asking "if you have the initiative" answered false for every player for the whole game, and
/// the false branch ran silently. This is the monarch's twin, and had been left out.
/// </para>
/// </summary>
[RuleRef("726.2")]
public sealed record InitiativeChanged(PlayerId Player) : GameEvent
{
    public override GameState Apply(GameState state) => state with { Initiative = Player };
}

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
        Turn = state.Turn with { Number = Number, ActivePlayer = ActivePlayer, Step = Step.Untap, IsExtraTurn = Extra, TurnOrderIndex = ActivePlayer.Value },
        Players = [.. state.Players.Select(p => p with { LandsPlayedThisTurn = 0, ObjectsDrawnThisTurn = 0 })],
        History = TurnHistory.Empty,
    };
}

[RuleRef("500.1")]
public sealed record StepBegan(Step Step) : GameEvent
{
    // CR 500.1: what was drawn in the step that ended is not drawn in this one (E2c ask 1).
    public override GameState Apply(GameState state) => state with
    {
        Turn = state.Turn with { Step = Step },
        Priority = PriorityState.None,
        Players = [.. state.Players.Select(p => p.StepBegins())],
    };
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
        CommanderReturnCandidates = Choice.Kind == Choices.ChoiceKinds.CommanderReturn && Choice.Context.Source is { } commander
            ? state.CommanderReturnCandidates.Remove(commander)
            : state.CommanderReturnCandidates,
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
/// <summary>A zone's order changed. <paramref name="Shuffle"/> says it was a shuffle (CR 701.20) rather than cards put back in a chosen order, which "would shuffle" replacements tell apart.</summary>
public sealed record ZoneReordered(ZoneKey Zone, ImmutableList<ObjectId> Order, bool Shuffle = false) : GameEvent
{
    public bool AwaitingRandomization { get; init; }

    public override GameState Apply(GameState state)
    {
        if (AwaitingRandomization)
            return state;
        var reordered = state with { Zones = state.Zones.SetItem(Zone, Order) };
        if (Shuffle || !state.In(Zone).SequenceEqual(Order))
            reordered = Library.ManualLibraryInspection.EndAfterReorder(reordered, Zone);
        if (Shuffle && Zone.Zone == Cards.Zone.Library)
            reordered = reordered with
            {
                LibraryInspections = reordered.LibraryInspections.RemoveRange(reordered.LibraryInspections
                    .Where(pair => pair.Value.Owner == Zone.Player).Select(pair => pair.Key)),
            };
        return Zone.Zone == Cards.Zone.Library && (Shuffle || !state.In(Zone).SequenceEqual(Order))
            ? Library.LibraryRevealCoordinator.EndReorderedDisclosures(reordered, Zone) : reordered;
    }
}


/// <summary>
/// One continuous effect in force was edited, or with a null effect ended: a prevention shield
/// that spent part of itself, CR 615.9, or a "prevent the next N" that reached zero.
/// </summary>
[RuleRef("615.9")]
public sealed record ActiveEffectChanged(int Id, ContinuousEffect? Effect) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        var index = state.ActiveEffects.FindIndex(e => e.Id == Id);
        if (index < 0)
            return state;
        var effects = Effect is null
            ? state.ActiveEffects.RemoveAt(index)
            : state.ActiveEffects.SetItem(index, state.ActiveEffects[index] with { Effect = Effect });
        return state with { ActiveEffects = effects };
    }
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
public sealed record DamageDealt(ObjectId Source, ObjectId? ToObject, PlayerId? ToPlayer, int Amount, bool Combat, bool FromCommander, bool Deathtouch = false) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        var h = state.History with { DamageDealtBy = state.History.DamageDealtBy.SetItem(Source, state.History.DamageDealtBy.GetValueOrDefault(Source) + Amount) };
        if (ToObject is { } id && state.Objects.TryGetValue(id, out var obj))
        {
            h = h with { DamageReceivedBy = h.DamageReceivedBy.SetItem(id, h.DamageReceivedBy.GetValueOrDefault(id) + Amount) };
            // CR 702.2b: damage from a source with deathtouch is remembered until state-based actions look.
            var damaged = obj with { Damage = obj.Damage + Amount, DealtDeathtouchDamage = obj.DealtDeathtouchDamage || (Deathtouch && Amount > 0) };
            return state with { Objects = state.Objects.SetItem(id, damaged), History = h };
        }

        if (ToPlayer is { } pid)
        {
            var p = state.Player(pid);
            // Keyed by the commander rather than the object it is right now: see CommanderIdentity.
            var commanderDamage = p.CommanderDamage;
            if (FromCommander && Identify(state, Source) is { } who)
            {
                commanderDamage = commanderDamage.SetItem(who, commanderDamage.GetValueOrDefault(who) + Amount);
            }
            h = h with { DamageReceivedByPlayer = h.DamageReceivedByPlayer.SetItem(pid, h.DamageReceivedByPlayer.GetValueOrDefault(pid) + Amount) };
            var pair = (Source, pid);
            h = h with { DamageDealtToPlayer = h.DamageDealtToPlayer.SetItem(pair, h.DamageDealtToPlayer.GetValueOrDefault(pair) + Amount) };
            return state with { Players = state.Players.SetItem(pid.Value, p with { Life = p.Life - Amount, CommanderDamage = commanderDamage }), History = h };
        }

        return state with { History = h };
    }

    /// <summary>
    /// Who this source is, as a commander. The object is normally still there when its damage
    /// is folded; when it is not - a commander that died dealing its damage - the last-known
    /// object carries the same owner and card, which is the whole point of the key.
    /// </summary>
    private static CommanderIdentity? Identify(GameState state, ObjectId source)
    {
        if (state.Objects.TryGetValue(source, out var live))
        {
            return CommanderIdentity.Of(live);
        }

        var last = state.History.LeftBattlefield.FirstOrDefault(o => o.Id == source)
            ?? state.History.Died.FirstOrDefault(o => o.Id == source);
        return last is null ? null : CommanderIdentity.Of(last);
    }
}

/// <summary>Mana entered a pool, CR 106.4. Positive amounts add; the mana subsystem spends with negative ones.</summary>
[RuleRef("106.4")]
/// <summary>Mana entered or left a pool, CR 106.4. <paramref name="SpentOn"/> is the spell or the ability's source a payment was for (CR 106.6a's "when that mana is spent"), null for production and for emptying.</summary>
public sealed record ManaChanged(PlayerId Player, ManaPool Delta, string Reason, ObjectId? SpentOn = null) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        var p = state.Player(Player);
        return state with { Players = state.Players.SetItem(Player.Value, p with { Mana = ApplyTo(p.Mana) }) };
    }

    internal ManaPool ApplyTo(ManaPool m) => ApplyTo(m, false)!;

    internal ManaPool? TryApplyTo(ManaPool m) => ApplyTo(m, true);

    private ManaPool? ApplyTo(ManaPool m, bool validate)
    {
        var restricted = ManaPool.FoldRestrictedCore(m.Restricted, Delta.Restricted, validate);
        var provenance = ManaPool.FoldProvenanceCore(m.Provenance, Delta.Provenance, validate);
        if (restricted is null || provenance is null)
            return null;
        var result = new ManaPool(m.White + Delta.White, m.Blue + Delta.Blue, m.Black + Delta.Black, m.Red + Delta.Red, m.Green + Delta.Green, m.Colorless + Delta.Colorless)
        {
            Restricted = restricted,
            Provenance = provenance,
        };
        return validate && (result.White < 0 || result.Blue < 0 || result.Black < 0 || result.Red < 0
            || result.Green < 0 || result.Colorless < 0) ? null : result;
    }
}

[RuleRef("106.4")]
public sealed record ManaPoolEmptied(PlayerId Player) : GameEvent
{
    public override GameState Apply(GameState state) =>
        state with { Players = state.Players.SetItem(Player.Value, state.Player(Player) with { Mana = ManaPool.Empty }) };
}

/// <summary>A spell was cast, CR 601: the object is already on the stack by an <see cref="ObjectMoved"/>; this names the moment for triggers.</summary>
[RuleRef("508")]
public sealed record AttackersDeclared(PlayerId Player, ImmutableDictionary<ObjectId, CombatTarget> Attackers) : GameEvent
{
    public ImmutableList<PlayerId> DefendingPlayers { get; init; } = [];

    public override GameState Apply(GameState state) => state with
    {
        Combat = new CombatState(Player, Attackers, ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty, AttackersDeclared: true, BlockersDeclared: false)
        {
            DefendingPlayers = DefendingPlayers.IsEmpty
                ? [.. Attackers.Values.Where(t => t.Player is not null).Select(t => t.Player!.Value).Distinct()]
                : DefendingPlayers,
        },
        History = state.History with { AttackedWith = state.History.AttackedWith.AddRange(Attackers.Keys) },
    };
}

[RuleRef("509")]
public sealed record BlockersDeclared(PlayerId Player, ImmutableDictionary<ObjectId, ImmutableList<ObjectId>> Blockers) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        if (state.Combat is not { } combat)
            return state;
        var declared = combat.BlockersDeclaredBy.Contains(Player) ? combat.BlockersDeclaredBy : combat.BlockersDeclaredBy.Add(Player);
        return state with
        {
            Combat = combat with
            {
                Blockers = combat.Blockers.SetItems(Blockers),
                BlockersDeclaredBy = declared,
                BlockersDeclared = combat.DefendingPlayers.Where(p => !state.Player(p).HasLost).All(declared.Contains),
            },
        };
    }
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
