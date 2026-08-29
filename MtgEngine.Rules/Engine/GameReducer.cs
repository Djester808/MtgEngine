using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
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

            // The request changes nothing on its own; the shuffle that answers it does.
            ShuffleRequested => state,
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
            CascadeRequested => state,
            FreeCastOffered offered => state.TryGetObject(offered.Id, out var free)
                ? state.WithObject(free with
                {
                    MayCastFree = true,
                    OfferedCost = offered.Cost,
                    ToHandIfCastDeclined = offered.ToHandIfDeclined,
                })
                : state,
            FreeCastLapsed lapsed => state.TryGetObject(lapsed.Id, out var stale)
                ? state.WithObject(stale with
                {
                    MayCastFree = false,
                    ToHandIfCastDeclined = false,
                })
                : state,
            CardForetold told => state.TryGetObject(told.Id, out var hidden)
                ? state.WithObject(hidden with { ForetoldOnTurn = told.Turn })
                : state,
            SpellBoughtBack bought => state.TryGetObject(bought.Id, out var paid)
                ? state.WithObject(paid with { WasBoughtBack = true })
                : state,
            JoinedCombat joined => state with
            {
                Combat = state.Combat with
                {
                    Attackers = state.Combat.Attackers.SetItem(joined.Id, joined.Target),
                },
            },
            CardPlotted plotted => state.TryGetObject(plotted.Id, out var laid)
                ? state.WithObject(laid with { PlottedOnTurn = plotted.Turn })
                : state,
            CardSuspended suspended => state.TryGetObject(suspended.Id, out var waiting)
                ? state.WithObject(waiting with
                {
                    TimeCounters = suspended.TimeCounters,
                    SuspendedBy = suspended.PlayerId,
                })
                : state,
            TimeCounterRemoved ticked => state.TryGetObject(ticked.Id, out var counting)
                ? state.WithObject(counting with { TimeCounters = ticked.Remaining })
                : state,
            CardsRevealed shown => shown.Cards.Aggregate(
                state,
                (running, id) => running.TryGetObject(id, out var card)
                    ? running.WithObject(card with { IsRevealed = true })
                    : running),
            SpellOffspring offspring => state.TryGetObject(offspring.Id, out var parent)
                ? state.WithObject(parent with { WasOffspring = true })
                : state,
            SpellBestowed bestowed => state.TryGetObject(bestowed.Id, out var asAura)
                ? state.WithObject(asAura with { WasBestowed = true })
                : state,
            SpellOverloaded loud => state.TryGetObject(loud.Id, out var everything)
                ? state.WithObject(everything with { WasOverloaded = true })
                : state,
            SpellAwakened roused => state.TryGetObject(roused.Id, out var stirring)
                ? state.WithObject(stirring with { WasAwakened = true })
                : state,
            SpellSneaked snuck => state.TryGetObject(snuck.Id, out var creeping)
                ? state.WithObject(creeping with { JoiningAgainst = snuck.Against })
                : state,
            SpellEvoked evoked => state.TryGetObject(evoked.Id, out var fleeting)
                ? state.WithObject(fleeting with { WasEvoked = true })
                : state,
            SpellDashed dashed => state.TryGetObject(dashed.Id, out var hasty)
                ? state.WithObject(hasty with { WasDashed = true })
                : state,
            SpellBlitzed blitzed => state.TryGetObject(blitzed.Id, out var quick)
                ? state.WithObject(quick with { WasBlitzed = true })
                : state,
            // CR 725.3: only one player can be the monarch, so this is a single field and the
            // previous monarch stops being one by the same assignment.
            MonarchChanged crowned => state with { MonarchId = crowned.PlayerId },

            CitysBlessingGained blessed => state.WithPlayer(
                state.GetPlayer(blessed.PlayerId) with { HasCitysBlessing = true }),
            SpellCopied copied => CopyOnStack(state, copied),
            HandChoiceRequested => state,
            ColorChoiceRequested => state,
            CreatureTypeChoiceRequested => state,
            ConniveRequested => state,
            ManifestDreadRequested => state,
            PopulateRequested => state,
            ExploitRequested => state,
            DiscoverRequested => state,
            RingBearerRequested => state,
            RingTempted tempted => state.WithPlayer(
                state.GetPlayer(tempted.PlayerId) with
                {
                    // Four is every ability the emblem has; a fifth temptation still happens and
                    // still chooses a bearer, it simply turns nothing further on.
                    RingTemptations = Math.Min(4, state.GetPlayer(tempted.PlayerId).RingTemptations + 1),
                }),
            RingBearerChosen bearer => state.WithPlayer(
                state.GetPlayer(bearer.PlayerId) with { RingBearer = bearer.Creature }),
            ClashRequested => state,
            ClashRevealed => state,
            CardPutOnBottom bottomed => PutOnBottom(state, bottomed),
            CardMayBePlayed playable => state.TryGetObject(playable.Id, out var loose)
                ? state.WithObject(loose with
                {
                    MayPlayUntilTurn = playable.ThroughOwnersNextTurn ? null : playable.UntilTurn,
                    MayPlayThroughOwnersNextTurn =
                        playable.ThroughOwnersNextTurn ? playable.UntilTurn : null,
                })
                : state,
            PlayWindowClosed closed => state.TryGetObject(closed.Id, out var spent)
                ? state.WithObject(spent with
                {
                    MayPlayUntilTurn = null,
                    MayPlayThroughOwnersNextTurn = null,
                })
                : state,
            SpellEscaped escaped => state.TryGetObject(escaped.Id, out var risen)
                ? state.WithObject(risen with { WasEscaped = true })
                : state,
            SpellPrototyped small => state.TryGetObject(small.StackId, out var shrunk)
                ? state.WithObject(shrunk with { WasPrototyped = true })
                : state,
            SpellWarped warped => state.TryGetObject(warped.Id, out var bent)
                ? state.WithObject(bent with { WasWarped = true })
                : state,
            CardWarpedToExile gone => state.TryGetObject(gone.Id, out var away)
                ? state.WithObject(away with { WarpedOnTurn = gone.Turn })
                : state,
            CreatureExploited => state,
            UntapChoiceRequested => state,
            CounterChoiceRequested => state,
            LibraryOrderRequested => state,
            LibraryOrdered ordered => Arrange(state, ordered),
            ExiledUntilLeaves held => state.TryGetObject(held.Id, out var exiled)
                ? state.WithObject(exiled with { ExiledBy = held.By })
                : state,
            PermanentTapped tapped => SetTapped(state, [tapped.Id], true),
            ManaPersistenceEnded over => state.WithPlayer(
                state.GetPlayer(over.PlayerId) with
                {
                    PersistentMana = ManaPool.Empty,
                    PersistentManaUntil = ManaPersistence.None,
                }),
            SuspendOnResolveRequested => state,
            // Inserted at the front: CR 500.7 takes the most recently created extra turn first.
            SacrificeUnlessPaidRequested => state,
            CipherRequested => state,
            LibraryEndChoiceRequested => state,
            SpellEncoded encoded => state.TryGetObject(encoded.CardId, out var written)
                ? state.WithObject(written with { EncodedOn = encoded.CreatureId })
                : state,
            ExtraTurnCreated extra => state with
            {
                ExtraTurns = state.ExtraTurns.Insert(0, extra.PlayerId),
            },
            ExtraTurnTaken => state with { ExtraTurns = state.ExtraTurns.RemoveAt(0) },
            ManaMadePersistent kept => state.WithPlayer(
                state.GetPlayer(kept.PlayerId) with
                {
                    PersistentMana = state.GetPlayer(kept.PlayerId).PersistentMana.Plus(kept.Kept),
                    PersistentManaUntil = kept.Until,
                }),
            ExtraLandDropGranted extra => state.WithPlayer(
                state.GetPlayer(extra.PlayerId) with
                {
                    ExtraLandDropsThisTurn =
                        state.GetPlayer(extra.PlayerId).ExtraLandDropsThisTurn + extra.Count,
                }),
            BecamePrepared ready => SetPrepared(state, ready.Id, true),
            Unprepared spent => SetPrepared(state, spent.Id, false),
            CardManifested manifested => state.TryGetObject(manifested.Id, out var hidden)
                && hidden.Permanent is { } asleep
                ? state.WithObject(hidden with
                {
                    Permanent = asleep with { IsFaceDown = true, IsManifested = true },
                })
                : state,
            PermanentTurned turned => state.TryGetObject(turned.Id, out var flipping)
                && flipping.Permanent is { } flipped
                ? state.WithObject(
                    flipping with
                    {
                        // Turning face up ends the manifest too: the designation is only ever
                        // about a face-down permanent (CR 701.40a).
                        Permanent = flipped with
                        {
                            IsFaceDown = turned.FaceDown,
                            IsManifested = turned.FaceDown && flipped.IsManifested,
                        },
                    })
                : state,
            PermanentTransformed turnedOver => Transform(state, turnedOver),
            DayNightChanged sky => state with { IsDay = sky.IsDay },
            SummoningSicknessCleared cleared => ClearSickness(state, cleared.Ids),
            LandDropUsed land => LandDrop(state, land),
            // Counted as it is cast, not as it resolves: a countered spell was still cast, and
            // the cards that ask about "your second spell each turn" are about the casting.
            SpellCastEvent cast => Cast(state, cast),
            StackObjectResolved => state,
            DamageCleared => ClearDamage(state),
            PermanentSaddled saddled => state.TryGetObject(saddled.Id, out var mount)
                && mount.Permanent is { } mounted
                ? state.WithObject(mount with { Permanent = mounted with { IsSaddled = true } })
                : state,
            UntapSkipped skipping => state.TryGetObject(skipping.Id, out var held)
                && held.Permanent is { } stuck
                ? state.WithObject(held with
                {
                    Permanent = stuck with { SkipsNextUntap = skipping.Skipping },
                })
                : state,
            DamageRemoved removed => state.TryGetObject(removed.Id, out var healed)
                && healed.Permanent is { } hurt
                ? state.WithObject(healed with
                {
                    Permanent = hurt with
                    {
                        DamageMarked = 0,
                        DamagedBy = [],
                        DealtDeathtouchDamage = false,
                    },
                })
                : state,
            ObjectCreated created => Create(state, created),
            PlayerLost lost => Lose(state, lost),
            GameEnded ended => state with { IsOver = true, WinnerId = ended.WinnerId },
            ContinuousEffectCreated created => CreateEffect(state, created),
            ContinuousEffectEnded ended2 => state with
            {
                FloatingEffects = state.FloatingEffects.RemoveAll(f => f.Id == ended2.EffectId),
            },
            EventReplaced => state,
            NothingHappened => state,

            BecameMonstrous monstrous => Changing(state, monstrous.Id, o => o with
            {
                Permanent = o.Permanent is { } was ? was with { IsMonstrous = true } : null,
            }),

            // A summary of events already folded in, so folding it again would double them.
            CombatDamageDealt => state,
            ManaAdded added => AddMana(state, added),
            CharacteristicChosen chosen => Changing(
                state, chosen.Id, o => o with { Chosen = chosen.Value }),
            StateTriggerArmed armed => state with
            {
                ArmedStateTriggers = armed.Armed
                    ? state.ArmedStateTriggers.Add(armed.Key)
                    : state.ArmedStateTriggers.Remove(armed.Key),
            },
            // The permission is clamped down to what is left, so mana spent out of the
            // persistent part stops being persistent. Without this, spending the kept mana and
            // then tapping a land would let the new mana inherit a permission it never had.
            ManaSpent spent => state.WithPlayer(
                state.GetPlayer(spent.PlayerId) with
                {
                    ManaPool = spent.Remaining,
                    PersistentMana =
                        state.GetPlayer(spent.PlayerId).PersistentMana.ClampedTo(spent.Remaining),
                }),
            ManaPoolsEmptied => EmptyPools(state),
            TargetsChosen chosen => state.WithObject(
                state.GetObject(chosen.StackId) with
                {
                    Targets = chosen.Targets,
                    VariableValue = chosen.VariableValue,
                    DamageDivision = chosen.DamageDivision ?? [],
                }),
            FizzledForIllegalTargets => state,
            AbilityActivated => state,
            ChoiceRequested asked => state with { Choice = asked.Choice },
            ChoiceMade => state with { Choice = null },
            MulliganTaken taken => state with
            {
                MulligansTaken = state.MulligansTaken.SetItem(taken.PlayerId, taken.MulligansTaken),
            },
            MulligansBegan => state with { IsMulliganing = true },
            CommanderDesignated designated => state.WithPlayer(
                state.GetPlayer(designated.PlayerId) with
                {
                    CommanderOracleId = designated.OracleId,
                }),
            CommanderCastFromCommandZone cast => state.WithPlayer(
                state.GetPlayer(cast.PlayerId) with
                {
                    CommanderCastsFromCommandZone = cast.TimesCast,
                }),
            CommanderDamageDealt damage => state.WithPlayer(
                state.GetPlayer(damage.PlayerId) with
                {
                    CommanderDamage = state.GetPlayer(damage.PlayerId).CommanderDamage
                        .SetItem(damage.CommanderOracleId, damage.Total),
                }),
            MulliganKept => state,
            MulligansFinished => state with { IsMulliganing = false },
            AttackersDeclared attackers => (state with
            {
                Combat = state.Combat with
                {
                    Attackers = attackers.Attackers,
                    AttackersDeclared = true,
                },
            }).WithPlayer(
                // Recorded even when the set is empty, and deliberately not: declaring no
                // attackers is not attacking (CR 508.1). The flag is about the player rather
                // than about the creatures, so it survives them dying.
                state.GetPlayer(state.ActivePlayerId) with
                {
                    AttackedThisTurn = state.GetPlayer(state.ActivePlayerId).AttackedThisTurn
                        || attackers.Attackers.Count > 0,
                }),
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
            HalfUnlocked unlocked => Changing(
                state,
                unlocked.Id,
                found => found.Permanent is null
                    ? found
                    : found with
                    {
                        Permanent = found.Permanent with
                        {
                            UnlockedHalves = found.Permanent.UnlockedHalves.Add(unlocked.Half),
                        },
                    }),
            WentOnAdventure adventuring => Changing(
                state,
                adventuring.Id,
                found => found with { OnAdventure = true }),
            BecameRenowned renowned => Changing(
                state,
                renowned.Id,
                found => found.Permanent is null
                    ? found
                    : found with { Permanent = found.Permanent with { IsRenowned = true } }),
            CaseSolved solved => Changing(
                state,
                solved.Id,
                found => found.Permanent is null
                    ? found
                    : found with { Permanent = found.Permanent with { IsSolved = true } }),
            ClassLevelChanged levelled => Changing(
                state,
                levelled.Id,
                found => found.Permanent is null
                    ? found
                    : found with { Permanent = found.Permanent with { Level = levelled.Level } }),
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
                    SubjectPlayer = triggered.SubjectPlayer,
                    SubjectObject = triggered.SubjectObject,
                    SubjectAmount = triggered.SubjectAmount,
                }),
            },
            PermanentAttached attached => Attach(state, attached),
            RegenerationShieldsChanged shields => Shield(state, shields),
            PreventionChanged prevent => Prevent(state, prevent),
            PreventionEffectCreated shield => state with
            {
                Preventions = state.Preventions.Add(shield.Effect),
            },
            RedirectionChanged redirect => Redirect(state, redirect),
            HandLookedAt => state,
            FreerunningEnabled ready => state.WithPlayer(
                state.GetPlayer(ready.PlayerId) with
                {
                    AssassinOrCommanderConnectedThisTurn = true,
                }),
            SpeedChanged speed => state.WithPlayer(
                state.GetPlayer(speed.PlayerId) with
                {
                    // CR 702.179a: 4 is as fast as anyone goes. Nothing lowers speed because
                    // nothing emits a lower number — the bound is all this has to enforce.
                    Speed = Math.Clamp(speed.Speed, 0, 4),
                    SpeedIncreasedThisTurn =
                        state.GetPlayer(speed.PlayerId).SpeedIncreasedThisTurn || speed.TurnIncrease,
                }),
            EnergyChanged energy => state.WithPlayer(
                state.GetPlayer(energy.PlayerId) with
                {
                    // CR 107.4c: energy is not mana and does not empty; it only ever moves by
                    // what an effect gives or a cost takes, and never below nothing.
                    Energy = Math.Max(0, state.GetPlayer(energy.PlayerId).Energy + energy.Delta),
                }),
            ExperienceCountersChanged experience => state.WithPlayer(
                state.GetPlayer(experience.PlayerId) with
                {
                    // Energy's rule, minus the spending: an experience counter is given and then
                    // kept for the rest of the game, so there is no arm anywhere that takes one
                    // away and no turn boundary that clears them.
                    ExperienceCounters = Math.Max(
                        0,
                        state.GetPlayer(experience.PlayerId).ExperienceCounters + experience.Delta),
                }),
            PlayerPreventionChanged shield => state.WithPlayer(
                state.GetPlayer(shield.PlayerId) with
                {
                    DamageToPrevent = Math.Max(
                        0, state.GetPlayer(shield.PlayerId).DamageToPrevent + shield.Delta),
                }),
            PoisonCountersChanged poison => state.WithPlayer(
                state.GetPlayer(poison.PlayerId) with
                {
                    PoisonCounters = Math.Max(
                        0, state.GetPlayer(poison.PlayerId).PoisonCounters + poison.Delta),
                }),
            // The looking changes nothing; the answer to the question it raises does. Same for a
            // discard the game has yet to ask about — the cards do not move until it is answered.
            LookAtTopRequested => state,
            DiscardRequested => state,
            LookAndTakeRequested => state,
            LibrarySearchRequested => state,
            ProliferateRequested => state,
            ChoosePermanentRequested => state,
            CoinFlipRequested => state,
            CoinFlipped => state,
            ModesChosen chosenModes => Changing(
                state, chosenModes.StackId, o => o with { ChosenModes = chosenModes.Modes }),
            SpellSquadded squad => Changing(
                state, squad.StackId, o => o with { SquadPaid = squad.Times }),
            CardsSpliced spliced => Changing(
                state, spliced.StackId, o => o with { Spliced = spliced.Cards }),
            SpellKicked kicked => Changing(
                state, kicked.StackId, o => o with { WasKicked = true }),
            SpellBargained bargained => Changing(
                state, bargained.StackId, o => o with { WasBargained = true }),
            SpellMultikicked many => Changing(
                state, many.Id, o => o with { TimesKicked = many.Times }),
            ManaColorsSpent spent => Changing(
                state, spent.StackId, o => o with { ManaSpent = spent.Spent }),
            OptionalPaymentRequested => state,
            DelayedTriggerCreated made => state with
            {
                Delayed = state.Delayed.Add(new DelayedTrigger
                {
                    Id = made.Id,
                    ControllerId = made.ControllerId,
                    SubjectId = made.SubjectId,
                    Step = made.Step,
                    EffectId = made.EffectId,
                    TurnCreated = made.TurnCreated,
                }),
            },
            // CR 603.7b: it fires once and is gone.
            DelayedTriggerFired fired => state with
            {
                Delayed = state.Delayed.RemoveAll(d => d.Id == fired.Id),
            },
            TriggerPutOnStack put => PutTriggerOnStack(state, put),
            TriggerRemovedForNoTargets gone => state with
            {
                // CR 603.3d: it stops waiting without ever becoming an object on the stack.
                PendingTriggers = state.PendingTriggers.RemoveAll(
                    t => t.SourceId == gone.SourceId
                        && string.Equals(t.AbilityId, gone.AbilityId, StringComparison.Ordinal)),
            },
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

    /// <summary>Puts a library into a stated order — the top N chosen, the rest untouched.</summary>
    /// <summary>Moves a card already in a library to the bottom of it (CR 701.30a).</summary>
    private static GameState PutOnBottom(GameState state, CardPutOnBottom e)
    {
        var player = state.GetPlayer(e.PlayerId);
        if (!player.Library.Contains(e.Id))
            return state;

        return state.WithPlayer(player with
        {
            Library = player.Library.Remove(e.Id).Add(e.Id),
        });
    }

    private static GameState Arrange(GameState state, LibraryOrdered e)
    {
        var player = state.GetPlayer(e.PlayerId);

        // The event names only the cards that were arranged; everything under them keeps its
        // place. Checked rather than assumed, because an order naming a card that is no longer
        // on top would silently rewrite the wrong part of the library.
        if (e.Order.Count > player.Library.Count
            || !e.Order.All(player.Library.Take(e.Order.Count).Contains))
        {
            throw new InvalidOperationException(
                $"Arrangement for {e.PlayerId:N} does not name the cards on top of the library.");
        }

        return state.WithPlayer(player with
        {
            Library = e.Order.AddRange(player.Library.Skip(e.Order.Count)),
        });
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

        // Everything that crosses the edge of the battlefield is written down once, and the dozen
        // questions the corpus asks about arrivals and departures are filters over those two
        // lists - CR 700.4's "died" among them, which is this move with the graveyard as its
        // destination. Noted as the move is folded, so a rebuilt game knows it too.
        if (e.From == Zone.Battlefield)
        {
            state = state with
            {
                DeparturesThisTurn = state.DeparturesThisTurn.Add(new BattlefieldDeparture(
                    e.OldId,
                    // CR 613.1b: the engine computes this and puts it on the move, because the
                    // stored controller is only where control started. Null is a hand-built event
                    // that never went through the engine; the stored answer is right for every
                    // permanent no control-changing effect has touched, which is all of them in
                    // that case.
                    e.LeavingControllerId ?? moving.ControllerId,
                    moving.Card,
                    e.To)),
            };
        }

        // Entering needs no such computing: a control-changing effect applies to a permanent that
        // is already there, so at the moment of entry the controller the mover names is the one
        // the card means by "entered the battlefield under your control".
        if (e.To == Zone.Battlefield)
        {
            state = state with
            {
                ArrivalsThisTurn = state.ArrivalsThisTurn.Add(
                    new BattlefieldArrival(e.NewId, e.ControllerId, moving.Card)),
            };
        }

        // CR 700.11: a player has descended when a permanent card is put into their graveyard
        // from anywhere - which is any zone at all, so the move's origin is not looked at. A
        // token is not a card (CR 111.7) and descends nobody, and the graveyard is always the
        // owner's (CR 400.3), so the player is the owner rather than whoever moved it.
        if (e.To == Zone.Graveyard
            && !moving.Card.CardTypes.HasFlag(CardType.Token)
            && IsPermanentCard(moving.Card))
        {
            var descending = state.GetPlayer(moving.OwnerId);
            state = state.WithPlayer(descending with
            {
                TimesDescendedThisTurn = descending.TimesDescendedThisTurn + 1,
            });
        }

        state = RemoveFrom(state, moving.Zone, moving.OwnerId, e.OldId);

        // CR 400.7. The object that arrives is a new one; the old identity stops existing, so a
        // stale reference fails to resolve instead of quietly finding something that came back.
        state = state with { Objects = state.Objects.Remove(e.OldId) };

        var (withTimestamp, timestamp) = state.TakeTimestamp();
        state = withTimestamp;

        var resolving = moving.Zone == Zone.Stack && e.To == Zone.Battlefield;

        state = state.WithObject(new GameObject
        {
            Id = e.NewId,
            PreviousId = e.OldId,
            Card = moving.Card,
            // CR 108.3: ownership never changes, whatever happens to control.
            OwnerId = moving.OwnerId,
            // A card in a library, hand, or graveyard is its owner's (CR 108.4); elsewhere the
            // mover says who controls it.
            ControllerId = e.To.IsPerPlayer() ? moving.OwnerId : e.ControllerId,
            Zone = e.To,
            Timestamp = timestamp,
            // CR 403.3: every object on the battlefield is a permanent, and only there.
            Permanent = e.To == Zone.Battlefield
                ? EnteringPermanent(moving.Card, state.TurnNumber)
                : null,

            // Stamped from the move rather than from a separate event, because the move is
            // already the whole fact: a card in a graveyard that got there by being discarded,
            // on this turn.
            DiscardedOnTurn = e is { Cause: MoveCause.Discard, To: Zone.Graveyard }
                ? state.TurnNumber
                : null,

            // CR 400.7 makes a zone change a new object that remembers nothing, and CR 607.2 is
            // the exception these two live in: an enters trigger reading "if it was kicked" is
            // linked to the kicker paid on the spell that became this permanent, so the fact has
            // to survive exactly one move - the resolution - and no other.
            WasKicked = resolving && moving.WasKicked,
            WasBargained = resolving && moving.WasBargained,

            // The same exception, one announcement along: "if X is 5 or more" on a ravenous
            // creature is linked to the X announced for the spell that became it (CR 607.2), and
            // an intervening-if is checked again as the ability resolves (CR 603.4) - by which
            // time the only object left is the permanent. Carried across exactly the one move
            // that turns a spell into a permanent, so nothing that merely arrives on the
            // battlefield inherits somebody else's X.
            VariableValue = resolving ? moving.VariableValue : 0,

            // CR 718.2: the alternative characteristics apply while it is a spell *or* while it
            // is a permanent, so unlike a cost flag this one has to outlive the stack.
            WasPrototyped = resolving && moving.WasPrototyped,
            TimesKicked = resolving ? moving.TimesKicked : 0,
            ManaSpent = resolving ? moving.ManaSpent : Mana.ManaPool.Empty,
            CastBy = e.Cause == MoveCause.Cast ? e.ControllerId
                : resolving ? moving.CastBy
                : null,
            CastFromZone = e.Cause == MoveCause.Cast ? e.From
                : resolving ? moving.CastFromZone
                : null,
        });

        // A draw is a move from library to hand, and the cards that count draws count that.
        if (e.Cause == MoveCause.Draw)
        {
            state = state.WithPlayer(
                state.GetPlayer(moving.OwnerId) with
                {
                    CardsDrawnThisTurn = state.GetPlayer(moving.OwnerId).CardsDrawnThisTurn + 1,
                });
        }

        // CR 400.3: an object headed for a library, graveyard, or hand goes to its owner's.
        return AddTo(state, e.To, moving.OwnerId, e.NewId, e.Position);
    }

    private static GameState Life(GameState state, LifeChanged e)
    {
        var player = state.GetPlayer(e.PlayerId);
        return state.WithPlayer(player with
        {
            Life = e.NewTotal,
            LostLifeThisTurn = player.LostLifeThisTurn || e.Delta < 0,
            LifeGainedThisTurn = player.LifeGainedThisTurn + int.Max(e.Delta, 0),
        });
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

    private static GameState AddMana(GameState state, ManaAdded e)
    {
        var player = state.GetPlayer(e.PlayerId);
        var pool = e.Restriction is { } only
            ? player.ManaPool.AddRestricted(
                new RestrictedMana(e.Color, only)
                {
                    FilterId = e.RestrictedTo,
                    FromZone = e.RestrictedToZone,
                    CommanderOnly = e.RestrictedToCommander,
                },
                e.Amount)
            : e.Color is null
                ? player.ManaPool.AddColorless(e.Amount)
                : player.ManaPool.Add(e.Color.Value, e.Amount);

        return state.WithPlayer(player with { ManaPool = pool });
    }

    /// <summary>
    /// Empties every mana pool at the end of a step or phase (CR 500.4), less whatever has been
    /// given permission to stay.
    /// </summary>
    private static GameState EmptyPools(GameState state)
    {
        foreach (var id in state.TurnOrder)
        {
            var player = state.GetPlayer(id);
            if (player.ManaPool.IsEmpty)
                continue;

            // Clamped rather than carried across, because the pool at this moment may hold mana
            // from elsewhere as well: what survives is the part the permission actually covers.
            var kept = player.PersistentMana.ClampedTo(player.ManaPool);

            state = state.WithPlayer(player with
            {
                ManaPool = kept,
                PersistentMana = kept,
            });
        }

        return state;
    }

    private static GameState DamagePlayer(GameState state, PlayerDamaged e)
    {
        // Noted on whoever controlled the creature that connected, before the damaged player is
        // updated, because both may be recorded from one event. Read off the source rather than
        // assumed to be the active player: a creature can deal combat damage on somebody else's
        // turn, and the two are different players exactly when it matters.
        if (e.IsCombat && state.TryGetObject(e.SourceId, out var dealer))
        {
            var attacker = state.GetPlayer(dealer.ControllerId);
            state = state.WithPlayer(
                attacker with { DealtCombatDamageToPlayerThisTurn = true });
        }

        // CR 120.3c: damage dealt to a player causes them to lose that much life.
        var player = state.GetPlayer(e.PlayerId);
        return state.WithPlayer(player with
        {
            Life = player.Life - e.Amount,

            // Noted here rather than counted from the log later: bloodthirst and the conditions
            // beside it are asked by predicates that see a state and nothing else.
            WasDealtDamageThisTurn = true,

            // The rule quoted above is the whole reason: damage *is* life loss, so a player who
            // has been dealt damage has lost life and every card asking that question must see
            // it. This was the one path to a smaller life total that did not say so, which made
            // "if an opponent lost life this turn" blind to combat - the commonest way it ever
            // happens - and left the speed rule (CR 702.179b) unable to fire from an attack.
            // Damage of 0 is not dealt at all (CR 120.8) and must not count as a loss.
            LostLifeThisTurn = player.LostLifeThisTurn || e.Amount > 0,
        });
    }

    /// <summary>
    /// The status a permanent arrives with (CR 306.5b).
    /// </summary>
    /// <remarks>
    /// A planeswalker enters with loyalty counters equal to its printed loyalty. That is not a
    /// replacement effect a card carries — it is what the rules do for every planeswalker — so
    /// it happens here rather than needing a definition per card.
    /// </remarks>
    /// <param name="card">The printed card the permanent arrives as.</param>
    /// <param name="turn">
    /// The turn now being taken, stamped on the permanent so that "as long as ~ entered this turn"
    /// has something to compare against. The move is already the whole fact, so no event carries
    /// it: the fold knows which turn it is folding.
    /// </param>
    private static PermanentState EnteringPermanent(CardDefinition card, int turn)
    {
        if (!card.CardTypes.HasFlag(CardType.Planeswalker) || card.StartingLoyalty is not { } loyalty)
            return new PermanentState { EnteredOnTurn = turn };

        return new PermanentState
        {
            EnteredOnTurn = turn,
            Counters = ImmutableDictionary<string, int>.Empty
                .Add(CounterKinds.Loyalty, loyalty),
        };
    }

    /// <summary>
    /// Whether this card would be a permanent if it resolved (CR 110.4a, 700.11).
    /// </summary>
    /// <remarks>
    /// The six permanent card types, asked as one mask rather than as six comparisons. Instants
    /// and sorceries are the whole of what it excludes, and <c>Tribal</c> and <c>Other</c> are
    /// deliberately not in the list: neither is a permanent type on its own, and a tribal card is
    /// always printed with one that is.
    /// </remarks>
    private static bool IsPermanentCard(CardDefinition card) =>
        (card.CardTypes
            & (CardType.Artifact | CardType.Creature | CardType.Enchantment
                | CardType.Land | CardType.Planeswalker | CardType.Battle)) != CardType.None;

    private static GameState Lose(GameState state, PlayerLost e)
    {
        var player = state.GetPlayer(e.PlayerId);
        return state.WithPlayer(player with { HasLost = true, LossReason = e.Reason });
    }

    private static GameState MarkDamage(GameState state, DamageMarked e)
    {
        if (!state.TryGetObject(e.Id, out var obj) || obj.Permanent is not { } permanent)
            return state;

        // CR 306.7: damage dealt to a planeswalker removes that many loyalty counters rather
        // than being marked on it — it has no toughness for damage to be compared against.
        if (obj.Card.CardTypes.HasFlag(CardType.Planeswalker))
        {
            var loyalty = permanent.Counters.GetValueOrDefault(CounterKinds.Loyalty);
            var left = Math.Max(0, loyalty - e.Amount);

            return state.WithObject(obj with
            {
                Permanent = permanent with
                {
                    Counters = left == 0
                        ? permanent.Counters.Remove(CounterKinds.Loyalty)
                        : permanent.Counters.SetItem(CounterKinds.Loyalty, left),
                },
            });
        }

        return state.WithObject(obj with
        {
            Permanent = permanent with
            {
                DamageMarked = permanent.DamageMarked + e.Amount,

                // Who dealt it, kept beside how much. A source of nothing is not recorded: an
                // event with no source is damage from a rule rather than from an object.
                DamagedBy = e.SourceId == default
                    ? permanent.DamagedBy
                    : permanent.DamagedBy.Add(e.SourceId),

                // CR 704.5h: remembered separately from the damage, because deathtouch destroys
                // regardless of how much was dealt.
                DealtDeathtouchDamage = permanent.DealtDeathtouchDamage || e.FromDeathtouch,
            },
        });
    }

    private static GameState ChangeCounters(GameState state, CountersChanged e)
    {
        if (!state.TryGetObject(e.Id, out var obj) || obj.Permanent is not { } permanent)
            return state;

        var count = permanent.Counters.GetValueOrDefault(e.Kind) + e.Delta;
        var counters = count <= 0
            ? permanent.Counters.Remove(e.Kind)
            : permanent.Counters.SetItem(e.Kind, count);

        return state.WithObject(obj with { Permanent = permanent with { Counters = counters } });
    }

    private static GameState CeaseToExist(GameState state, ObjectCeasedToExist e)
    {
        if (!state.TryGetObject(e.Id, out var obj))
            return state;
        state = RemoveFrom(state, obj.Zone, obj.OwnerId, e.Id);
        return state with { Objects = state.Objects.Remove(e.Id) };
    }

    /// <summary>Puts a copy of a spell on the stack, above what it copied (CR 707.10).</summary>
    private static GameState CopyOnStack(GameState state, SpellCopied e)
    {
        var (withTimestamp, timestamp) = state.TakeTimestamp();
        state = withTimestamp;

        state = state.WithObject(new GameObject
        {
            Id = e.Id,
            Card = e.Card,
            // CR 707.10: the copy is controlled by whoever made it, which need not be whoever
            // controls the spell it copies.
            OwnerId = e.ControllerId,
            ControllerId = e.ControllerId,
            Zone = Zone.Stack,
            Timestamp = timestamp,
            Targets = e.Targets,
            IsCopy = true,
        });

        return state.With(Zone.Stack, e.Id);
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
            Targets = e.Targets,
            ChosenModes = e.Modes,
            Ability = new AbilityOnStack
            {
                SourceId = e.SourceId,
                AbilityId = e.AbilityId,
                Text = e.Text,
                SubjectPlayer = e.SubjectPlayer,
                SubjectObject = e.SubjectObject,
                SubjectAmount = e.SubjectAmount,
                JoiningAgainst = e.JoiningAgainst,
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
            // CR 111.1: a token is created on the battlefield and was never anywhere else, so it
            // entered on this turn exactly as a card resolving into play did.
            Permanent = e.Zone == Zone.Battlefield
                ? EnteringPermanent(e.Card, state.TurnNumber)
                : null,
        });

        // And it entered, which is the other arm of that same fact. A token created and then
        // sacrificed in one turn still entered under its controller's control that turn, which is
        // exactly why the arrivals are recorded rather than swept off the battlefield later.
        if (e.Zone == Zone.Battlefield)
        {
            state = state with
            {
                ArrivalsThisTurn = state.ArrivalsThisTurn.Add(
                    new BattlefieldArrival(e.Id, e.ControllerId, e.Card)),
            };
        }

        return AddTo(state, e.Zone, e.OwnerId, e.Id, e.Position);
    }

    /// <summary>
    /// Notes a spell against the player who cast it (CR 601.2i).
    /// </summary>
    /// <remarks>
    /// Three facts off one event, and all of them read from the object on the stack: the spell is
    /// already there by the time the casting is complete, which is what lets the reducer see what
    /// was cast without the event having to carry it.
    /// <para>
    /// The card is kept as well as the counts because "your first enchantment spell each turn"
    /// and its siblings ask about a kind, and a kind is not something two counters can be made to
    /// answer — see <see cref="PlayerState.SpellCardsCastThisTurn"/>. A spell whose object cannot
    /// be found is still counted and simply not described, so the counts never disagree about how
    /// many spells there were.
    /// </para>
    /// </remarks>
    private static GameState Cast(GameState state, SpellCastEvent e)
    {
        var player = state.GetPlayer(e.PlayerId);
        var known = state.TryGetObject(e.StackId, out var spell);

        return state.WithPlayer(player with
        {
            SpellsCastThisTurn = player.SpellsCastThisTurn + 1,
            NoncreatureSpellsCastThisTurn = player.NoncreatureSpellsCastThisTurn
                + (known && !spell.Card.CardTypes.HasFlag(CardType.Creature) ? 1 : 0),
            SpellCardsCastThisTurn = known
                ? player.SpellCardsCastThisTurn.Add(spell.Card)
                : player.SpellCardsCastThisTurn,
        });
    }

    private static GameState BeginTurn(GameState state, TurnBegan e)
    {
        // CR 505.6b's allowance is per turn, so it resets for everyone, not only the new active
        // player: an effect can let a player play a land on someone else's turn.
        var players = state.Players;
        foreach (var (id, player) in players)
            players = players.SetItem(
                id,
                player with
                {
                    LandsPlayedThisTurn = 0,
                    ExtraLandDropsThisTurn = 0,
                    PersistentMana = ManaPool.Empty,
                    PersistentManaUntil = ManaPersistence.None,
                    AttackedThisTurn = false,
                    WasDealtDamageThisTurn = false,
                    DealtCombatDamageToPlayerThisTurn = false,
                    AssassinOrCommanderConnectedThisTurn = false,
                    SpellsCastLastTurn = player.SpellsCastThisTurn,
                    SpellsCastThisTurn = 0,
                    CardsDrawnThisTurn = 0,
                    SpeedIncreasedThisTurn = false,
                    LostLifeThisTurn = false,
                    LifeGainedThisTurn = 0,
                    NoncreatureSpellsCastThisTurn = 0,
                    SpellCardsCastThisTurn = [],

                    // CR 700.11 is a per-turn count like the rest. The experience counters beside
                    // it deliberately are not: nothing in the game takes one away.
                    TimesDescendedThisTurn = 0,
                });

        return state with
        {
            TurnNumber = e.TurnNumber,
            ActivePlayerId = e.ActivePlayerId,
            PreviousActivePlayerId = state.TurnNumber == 0 ? null : state.ActivePlayerId,
            Players = players,
            ArrivalsThisTurn = [],
            DeparturesThisTurn = [],
        };
    }

    /// <summary>Adds or spends a regeneration shield (CR 701.19).</summary>
    private static GameState Shield(GameState state, RegenerationShieldsChanged e)
    {
        if (!state.TryGetObject(e.Id, out var obj) || obj.Permanent is null)
            return state;

        if (obj.Permanent is not { } permanent)
            return state;

        return state.WithObject(obj with
        {
            Permanent = permanent with
            {
                RegenerationShields = Math.Max(0, permanent.RegenerationShields + e.Delta),
            },
        });
    }

    /// <summary>Adds or spends a redirection (CR 614.1b).</summary>
    /// <remarks>
    /// The destination is cleared when the last point is spent, so a permanent that has finished
    /// redirecting is not still pointing at something.
    /// </remarks>
    private static GameState Redirect(GameState state, RedirectionChanged e)
    {
        if (!state.TryGetObject(e.Id, out var obj) || obj.Permanent is not { } permanent)
            return state;

        var left = Math.Max(0, permanent.DamageToRedirect + e.Delta);

        return state.WithObject(obj with
        {
            Permanent = permanent with
            {
                DamageToRedirect = left,
                RedirectDamageTo = left > 0 ? e.To ?? permanent.RedirectDamageTo : null,
            },
        });
    }

    /// <summary>Adds or spends a prevention shield (CR 615.1).</summary>
    private static GameState Prevent(GameState state, PreventionChanged e)
    {
        if (!state.TryGetObject(e.Id, out var obj) || obj.Permanent is null)
            return state;

        if (obj.Permanent is not { } permanent)
            return state;

        return state.WithObject(obj with
        {
            Permanent = permanent with
            {
                DamageToPrevent = Math.Max(0, permanent.DamageToPrevent + e.Delta),
            },
        });
    }

    /// <summary>Attaches one permanent to another, or to nothing (CR 701.3).</summary>
    private static GameState Attach(GameState state, PermanentAttached e)
    {
        // A fold has to be total. Every other arm asks with TryGetObject and this one demanded,
        // so an attach naming something that is not there took the whole game down rather than
        // doing nothing - and "not there" is ordinary: an Aura whose host left in response, or an
        // ability resolving after its source was destroyed.
        if (!state.TryGetObject(e.Id, out var obj) || obj.Permanent is not { } permanent)
            return state;

        return state.WithObject(obj with
        {
            Permanent = permanent with { AttachedTo = e.To, AttachedToPlayer = e.ToPlayer },
        });
    }

    /// <summary>Turns a permanent's prepared designation on or off.</summary>
    private static GameState SetPrepared(GameState state, ObjectId id, bool prepared)
    {
        if (!state.TryGetObject(id, out var card) || card.Permanent is not { } permanent)
            return state;

        return state.WithObject(card with
        {
            Permanent = permanent with { IsPrepared = prepared },
        });
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
                || (obj.Permanent.DamageMarked == 0
                    && !obj.Permanent.DealtDeathtouchDamage

                    // An unspent shield is a reason to visit this permanent in its own right.
                    // Left out of the guard, the sweep skipped exactly the creature that was
                    // never damaged - the one the shield had done its job for.
                    && obj.Permanent.DamageToPrevent == 0

                    // CR 514.2: "until end of turn" ends at the same moment damage is removed,
                    // and being saddled is until end of turn (CR 702.171a). Cleared here rather
                    // than by an event of its own, because this *is* the moment.
                    && !obj.Permanent.IsSaddled))
            {
                continue;
            }

            state = state.WithObject(obj with
            {
                Permanent = obj.Permanent with
                {
                    DamageMarked = 0,

                    // CR 514.2 again: the shield reads "this turn" and this is when the turn
                    // ends. It was left standing, so a shield bought on one turn was still
                    // soaking damage on the next - and on every turn after that.
                    DamageToPrevent = 0,

                    // CR 514.2: "the next N damage this turn" ends with the turn, exactly as a
                    // shield does, and for the same reason.
                    DamageToRedirect = 0,
                    RedirectDamageTo = null,
                    IsSaddled = false,
                    DamagedBy = [],
                    DealtDeathtouchDamage = false,
                },
            });
        }

        // The players' shields come down in the same breath and for the same reason, so the two
        // halves of "prevent the next N damage this turn" cannot expire on different schedules.
        foreach (var id in state.Players.Keys)
        {
            if (state.GetPlayer(id).DamageToPrevent != 0)
                state = state.WithPlayer(state.GetPlayer(id) with { DamageToPrevent = 0 });
        }

        // CR 514.2: "this turn" ends at the same moment damage is removed, and a described
        // prevention effect reads it the same way a shield does. Derived from the turn it was
        // made on rather than announced by an event of its own - this *is* the moment, and an
        // event saying so would be a second place for the two to disagree.
        if (state.Preventions.Any(p => p.UntilEndOfTurn <= state.TurnNumber))
        {
            state = state with
            {
                Preventions = state.Preventions.RemoveAll(
                    p => p.UntilEndOfTurn <= state.TurnNumber),
            };
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

    /// <summary>
    /// Turns a permanent to another of its faces (CR 712.2).
    /// </summary>
    /// <remarks>
    /// The object's card is swapped for the face's characteristics rather than the face being
    /// remembered alongside it. That is the whole trick: the layers, the ability source, the
    /// legality checks and the view all read <c>obj.Card</c>, and every one of them then reads
    /// the face the permanent is on without a line of change. The face list travels with the
    /// swapped-in definition, so it can turn back.
    /// <para>
    /// A card with no faces, or an index it does not have, is left exactly as it was: a fold has
    /// to be total, and an event naming a face that is not there is a bug elsewhere rather than a
    /// reason to lose the permanent.
    /// </para>
    /// </remarks>
    private static GameState Transform(GameState state, PermanentTransformed e)
    {
        if (!state.TryGetObject(e.Id, out var permanent)
            || permanent.Permanent is not { } onBattlefield
            || permanent.Card.Faces.Count <= e.FaceIndex
            || e.FaceIndex < 0)
        {
            return state;
        }

        return state.WithObject(permanent with
        {
            Card = MtgEngine.Rules.Cards.CardFaces.Definition(permanent.Card, e.FaceIndex),
            Permanent = onBattlefield with { FaceIndex = e.FaceIndex },
        });
    }

    /// <summary>
    /// Changes one object if it is still there, and does nothing at all if it is not.
    /// </summary>
    /// <remarks>
    /// **A fold has to be total.** These arms all asked for the object outright, and an event
    /// naming something that has since left then took the whole game down rather than folding to
    /// "nothing happens" - which is what the rules say happens to an effect whose object is gone.
    /// It is not hypothetical: an ability that attached its source to a target crashed a game
    /// this way, because the source was an Aura that state-based actions had already buried.
    /// <para>
    /// One helper rather than a guard per arm, so the next arm added gets the rule for free.
    /// </para>
    /// </remarks>
    private static GameState Changing(
        GameState state, ObjectId id, Func<GameObject, GameObject> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return state.TryGetObject(id, out var found) ? state.WithObject(change(found)) : state;
    }
}
