using System.Collections.Immutable;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Engine;

/// <summary>
/// Folds events into state. The only place a <see cref="GameState"/> is ever built.
/// </summary>
/// <remarks>
/// Everything that changes the game does so by emitting an event and letting this apply it, so
/// there is exactly one description of what any change does. The rule that keeps it honest:
/// <b>the reducer decides nothing.</b> It contains no legality checks, no dice, and no choices —
/// those all happen before an event is emitted. Give it the same events and it gives back the
/// same state, which is what makes <see cref="Replay"/> exact.
/// </remarks>
public static class GameReducer
{
    /// <summary>Rebuilds a game from its log. The first event must be <see cref="GameStarted"/>.</summary>
    public static GameState Replay(IEnumerable<GameEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        GameState? state = null;
        foreach (var e in events)
        {
            if (state is null)
            {
                if (e is not GameStarted started)
                    throw new InvalidOperationException(
                        $"A log has to begin with {nameof(GameStarted)}, not {e.GetType().Name}.");

                state = Start(started);
                continue;
            }

            state = Apply(state, e);
        }

        return state ?? throw new InvalidOperationException("An empty log is not a game.");
    }

    /// <summary>Applies one event to a game already under way.</summary>
    public static GameState Apply(GameState state, GameEvent e)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(e);

        return e switch
        {
            GameStarted => throw new InvalidOperationException("A game can only start once."),
            LibraryShuffled shuffled => Shuffle(state, shuffled),
            ObjectMoved moved => Move(state, moved),
            LifeChanged life => Life(state, life),
            DrawFromEmptyLibraryAttempted drawn => EmptyDraw(state, drawn),
            TurnBegan turn => BeginTurn(state, turn),
            StepBegan step => state with { CurrentStep = step.Step },
            PriorityGranted granted => state with
            {
                // CR 117.3c and 117.4: anything happening breaks the run of passes.
                Priority = new PriorityState { Holder = granted.PlayerId },
            },
            PriorityPassed passed => state with
            {
                Priority = state.Priority with
                {
                    Holder = passed.NextPlayerId,
                    Passed = state.Priority.Passed.Add(passed.PlayerId),
                },
            },
            PriorityWithdrawn => state with { Priority = new PriorityState() },
            PermanentsUntapped untapped => SetTapped(state, untapped.Ids, false),
            PermanentTapped tapped => SetTapped(state, [tapped.Id], true),
            SummoningSicknessCleared cleared => ClearSickness(state, cleared.Ids),
            LandDropUsed land => LandDrop(state, land),
            SpellCastEvent => state,
            StackObjectResolved => state,
            DamageCleared => ClearDamage(state),
            ObjectCreated created => Create(state, created),
            PlayerLost lost => Lose(state, lost),
            GameEnded ended => state with { IsOver = true, WinnerId = ended.WinnerId },
            ContinuousEffectCreated created => CreateEffect(state, created),
            ContinuousEffectEnded ended2 => state with
            {
                FloatingEffects = state.FloatingEffects.RemoveAll(f => f.Id == ended2.EffectId),
            },
            EventReplaced => state,
            AttackersDeclared attackers => state with
            {
                Combat = state.Combat with
                {
                    Attackers = attackers.Attackers,
                    AttackersDeclared = true,
                },
            },
            BlockersDeclared blockers => state with
            {
                Combat = state.Combat with
                {
                    Blockers = blockers.Blockers,
                    // CR 509.1h: blocked-ness is decided here and does not change when the
                    // blockers leave.
                    Blocked = [.. blockers.Blockers.Where(kv => !kv.Value.IsEmpty).Select(kv => kv.Key)],
                    BlockersDeclared = true,
                },
            },
            PlayerDamaged damaged => DamagePlayer(state, damaged),
            CombatDamageStepDone => state with
            {
                Combat = state.Combat with { DamageStepsDone = state.Combat.DamageStepsDone + 1 },
            },
            CombatEnded => state with { Combat = new CombatState() },
            DamageMarked damage => MarkDamage(state, damage),
            CountersChanged counters => ChangeCounters(state, counters),
            ObjectCeasedToExist gone => CeaseToExist(state, gone),
            AbilityTriggered triggered => state with
            {
                PendingTriggers = state.PendingTriggers.Add(new PendingTrigger
                {
                    SourceId = triggered.SourceId,
                    AbilityId = triggered.AbilityId,
                    Text = triggered.Text,
                    ControllerId = triggered.ControllerId,
                }),
            },
            TriggerPutOnStack put => PutTriggerOnStack(state, put),
            _ => throw new InvalidOperationException($"No reducer for {e.GetType().Name}."),
        };
    }

    // ---- One method per event ------------------------------------------------------------

    private static GameState Start(GameStarted e)
    {
        var objects = ImmutableDictionary.CreateBuilder<ObjectId, GameObject>();
        var players = ImmutableDictionary.CreateBuilder<Guid, PlayerState>();
        var turnOrder = ImmutableList.CreateBuilder<Guid>();
        var timestamp = 1L;

        foreach (var seat in e.Seats)
        {
            turnOrder.Add(seat.PlayerId);

            var library = ImmutableList.CreateBuilder<ObjectId>();
            foreach (var dealt in seat.Deck)
            {
                objects.Add(dealt.Id, new GameObject
                {
                    Id = dealt.Id,
                    Card = dealt.Card,
                    OwnerId = seat.PlayerId,
                    ControllerId = seat.PlayerId,
                    Zone = Zone.Library,
                    Timestamp = timestamp++,
                });
                library.Add(dealt.Id);
            }

            players.Add(seat.PlayerId, new PlayerState
            {
                PlayerId = seat.PlayerId,
                Name = seat.Name,
                Life = seat.StartingLife,
                Library = library.ToImmutable(),
            });
        }

        return new GameState
        {
            GameId = e.GameId,
            Objects = objects.ToImmutable(),
            Players = players.ToImmutable(),
            TurnOrder = turnOrder.ToImmutable(),
            ActivePlayerId = e.StartingPlayerId,
            // Turn 1 begins when the first turn does, which is slice 2's business. A game that
            // has been dealt but not begun is not on turn 1 yet.
            TurnNumber = 0,
            NextTimestamp = timestamp,
        };
    }

    private static GameState Shuffle(GameState state, LibraryShuffled e)
    {
        var player = state.GetPlayer(e.PlayerId);

        if (e.Order.Count != player.Library.Count)
            throw new InvalidOperationException(
                $"Shuffle of {e.PlayerId:N} lists {e.Order.Count} cards for a library of {player.Library.Count}.");

        return state.WithPlayer(player with { Library = e.Order });
    }

    private static GameState Move(GameState state, ObjectMoved e)
    {
        var moving = state.GetObject(e.OldId);

        if (moving.Zone != e.From)
            throw new InvalidOperationException(
                $"{e.OldId} is in {moving.Zone}, but the move says it is leaving {e.From}.");

        state = RemoveFrom(state, moving.Zone, moving.OwnerId, e.OldId);

        // CR 400.7. The object that arrives is a new one; the old identity stops existing, so a
        // stale reference fails to resolve instead of quietly finding something that came back.
        state = state with { Objects = state.Objects.Remove(e.OldId) };

        var (withTimestamp, timestamp) = state.TakeTimestamp();
        state = withTimestamp;

        state = state.WithObject(new GameObject
        {
            Id = e.NewId,
            Card = moving.Card,
            // CR 108.3: ownership never changes, whatever happens to control.
            OwnerId = moving.OwnerId,
            // A card in a library, hand, or graveyard is its owner's (CR 108.4); elsewhere the
            // mover says who controls it.
            ControllerId = e.To.IsPerPlayer() ? moving.OwnerId : e.ControllerId,
            Zone = e.To,
            Timestamp = timestamp,
            // CR 403.3: every object on the battlefield is a permanent, and only there.
            Permanent = e.To == Zone.Battlefield ? new PermanentState() : null,
        });

        // CR 400.3: an object headed for a library, graveyard, or hand goes to its owner's.
        return AddTo(state, e.To, moving.OwnerId, e.NewId, e.Position);
    }

    private static GameState Life(GameState state, LifeChanged e)
    {
        var player = state.GetPlayer(e.PlayerId);
        return state.WithPlayer(player with { Life = e.NewTotal });
    }

    private static GameState EmptyDraw(GameState state, DrawFromEmptyLibraryAttempted e)
    {
        var player = state.GetPlayer(e.PlayerId);
        return state.WithPlayer(player with { HasAttemptedDrawFromEmptyLibrary = true });
    }

    private static GameState CreateEffect(GameState state, ContinuousEffectCreated e)
    {
        var (withTimestamp, timestamp) = state.TakeTimestamp();

        return withTimestamp with
        {
            FloatingEffects = withTimestamp.FloatingEffects.Add(new FloatingEffect
            {
                Id = e.EffectId,
                DefinitionId = e.DefinitionId,
                AffectedIds = e.AffectedIds,
                Timestamp = timestamp,
                UntilEndOfTurn = e.UntilEndOfTurn,
            }),
        };
    }

    private static GameState DamagePlayer(GameState state, PlayerDamaged e)
    {
        // CR 120.3c: damage dealt to a player causes them to lose that much life.
        var player = state.GetPlayer(e.PlayerId);
        return state.WithPlayer(player with { Life = player.Life - e.Amount });
    }

    private static GameState Lose(GameState state, PlayerLost e)
    {
        var player = state.GetPlayer(e.PlayerId);
        return state.WithPlayer(player with { HasLost = true, LossReason = e.Reason });
    }

    private static GameState MarkDamage(GameState state, DamageMarked e)
    {
        var obj = state.GetObject(e.Id);
        var permanent = obj.Permanent
            ?? throw new InvalidOperationException($"{e.Id} is not on the battlefield.");

        return state.WithObject(obj with
        {
            Permanent = permanent with
            {
                DamageMarked = permanent.DamageMarked + e.Amount,
                // CR 704.5h: remembered separately from the damage, because deathtouch destroys
                // regardless of how much was dealt.
                DealtDeathtouchDamage = permanent.DealtDeathtouchDamage || e.FromDeathtouch,
            },
        });
    }

    private static GameState ChangeCounters(GameState state, CountersChanged e)
    {
        var obj = state.GetObject(e.Id);
        var permanent = obj.Permanent
            ?? throw new InvalidOperationException($"{e.Id} is not on the battlefield.");

        var count = permanent.Counters.GetValueOrDefault(e.Kind) + e.Delta;
        var counters = count <= 0
            ? permanent.Counters.Remove(e.Kind)
            : permanent.Counters.SetItem(e.Kind, count);

        return state.WithObject(obj with { Permanent = permanent with { Counters = counters } });
    }

    private static GameState CeaseToExist(GameState state, ObjectCeasedToExist e)
    {
        var obj = state.GetObject(e.Id);
        state = RemoveFrom(state, obj.Zone, obj.OwnerId, e.Id);
        return state with { Objects = state.Objects.Remove(e.Id) };
    }

    private static GameState PutTriggerOnStack(GameState state, TriggerPutOnStack e)
    {
        var (withTimestamp, timestamp) = state.TakeTimestamp();
        state = withTimestamp;

        state = state.WithObject(new GameObject
        {
            Id = e.Id,
            Card = e.SourceCard,
            OwnerId = e.ControllerId,
            ControllerId = e.ControllerId,
            Zone = Zone.Stack,
            Timestamp = timestamp,
            Ability = new AbilityOnStack
            {
                SourceId = e.SourceId,
                AbilityId = e.AbilityId,
                Text = e.Text,
            },
        });

        // CR 603.3: it becomes the topmost object on the stack, and stops being pending.
        return (state with
        {
            PendingTriggers = state.PendingTriggers.RemoveAll(
                t => t.SourceId == e.SourceId && string.Equals(t.AbilityId, e.AbilityId, StringComparison.Ordinal)),
        }).With(Zone.Stack, e.Id);
    }

    /// <summary>Adds to a shared zone at the top. Per-player zones go through AddTo.</summary>
    private static GameState With(this GameState state, Zone zone, ObjectId id) =>
        AddTo(state, zone, Guid.Empty, id, ZonePosition.Top);

    private static GameState Create(GameState state, ObjectCreated e)
    {
        var (withTimestamp, timestamp) = state.TakeTimestamp();
        state = withTimestamp;

        state = state.WithObject(new GameObject
        {
            Id = e.Id,
            Card = e.Card,
            OwnerId = e.OwnerId,
            ControllerId = e.Zone.IsPerPlayer() ? e.OwnerId : e.ControllerId,
            Zone = e.Zone,
            Timestamp = timestamp,
            Permanent = e.Zone == Zone.Battlefield ? new PermanentState() : null,
        });

        return AddTo(state, e.Zone, e.OwnerId, e.Id, e.Position);
    }

    private static GameState BeginTurn(GameState state, TurnBegan e)
    {
        // CR 505.6b's allowance is per turn, so it resets for everyone, not only the new active
        // player: an effect can let a player play a land on someone else's turn.
        var players = state.Players;
        foreach (var (id, player) in players)
            players = players.SetItem(id, player with { LandsPlayedThisTurn = 0 });

        return state with
        {
            TurnNumber = e.TurnNumber,
            ActivePlayerId = e.ActivePlayerId,
            Players = players,
        };
    }

    private static GameState SetTapped(GameState state, IReadOnlyList<ObjectId> ids, bool tapped)
    {
        foreach (var id in ids)
        {
            var obj = state.GetObject(id);
            var permanent = obj.Permanent
                ?? throw new InvalidOperationException($"{id} is not on the battlefield.");
            state = state.WithObject(obj with { Permanent = permanent with { IsTapped = tapped } });
        }

        return state;
    }

    private static GameState ClearSickness(GameState state, IReadOnlyList<ObjectId> ids)
    {
        foreach (var id in ids)
        {
            var obj = state.GetObject(id);
            if (obj.Permanent is null)
                continue;

            state = state.WithObject(
                obj with { Permanent = obj.Permanent with { HasSummoningSickness = false } });
        }

        return state;
    }

    private static GameState LandDrop(GameState state, LandDropUsed e)
    {
        var player = state.GetPlayer(e.PlayerId);
        return state.WithPlayer(player with { LandsPlayedThisTurn = player.LandsPlayedThisTurn + 1 });
    }

    private static GameState ClearDamage(GameState state)
    {
        foreach (var id in state.Battlefield)
        {
            var obj = state.GetObject(id);
            if (obj.Permanent is null
                || (obj.Permanent.DamageMarked == 0 && !obj.Permanent.DealtDeathtouchDamage))
            {
                continue;
            }

            state = state.WithObject(obj with
            {
                Permanent = obj.Permanent with { DamageMarked = 0, DealtDeathtouchDamage = false },
            });
        }

        return state;
    }

    // ---- Zone list plumbing ---------------------------------------------------------------

    private static GameState RemoveFrom(GameState state, Zone zone, Guid ownerId, ObjectId id)
    {
        if (zone.IsPerPlayer())
        {
            var player = state.GetPlayer(ownerId);
            return state.WithPlayer(zone switch
            {
                Zone.Library => player with { Library = Without(player.Library, id) },
                Zone.Hand => player with { Hand = Without(player.Hand, id) },
                Zone.Graveyard => player with { Graveyard = Without(player.Graveyard, id) },
                _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, null),
            });
        }

        return zone switch
        {
            Zone.Battlefield => state with { Battlefield = Without(state.Battlefield, id) },
            Zone.Stack => state with { Stack = Without(state.Stack, id) },
            Zone.Exile => state with { Exile = Without(state.Exile, id) },
            Zone.Command => state with { Command = Without(state.Command, id) },
            _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, null),
        };
    }

    private static GameState AddTo(
        GameState state, Zone zone, Guid ownerId, ObjectId id, ZonePosition position)
    {
        if (zone.IsPerPlayer())
        {
            var player = state.GetPlayer(ownerId);
            return state.WithPlayer(zone switch
            {
                Zone.Library => player with { Library = With(player.Library, id, position) },
                Zone.Hand => player with { Hand = With(player.Hand, id, position) },
                Zone.Graveyard => player with { Graveyard = With(player.Graveyard, id, position) },
                _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, null),
            });
        }

        return zone switch
        {
            Zone.Battlefield => state with { Battlefield = With(state.Battlefield, id, position) },
            Zone.Stack => state with { Stack = With(state.Stack, id, position) },
            Zone.Exile => state with { Exile = With(state.Exile, id, position) },
            Zone.Command => state with { Command = With(state.Command, id, position) },
            _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, null),
        };
    }

    private static ImmutableList<ObjectId> Without(ImmutableList<ObjectId> zone, ObjectId id)
    {
        var index = zone.IndexOf(id);
        return index < 0
            ? throw new InvalidOperationException($"{id} is not in the zone it is leaving.")
            : zone.RemoveAt(index);
    }

    /// <summary>Index 0 is the top of every ordered zone; see <see cref="PlayerState"/>.</summary>
    private static ImmutableList<ObjectId> With(
        ImmutableList<ObjectId> zone, ObjectId id, ZonePosition position) =>
        position == ZonePosition.Top ? zone.Insert(0, id) : zone.Add(id);
}
