using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Engine;

/// <summary>
/// The checks the game runs on itself (CR 704).
/// </summary>
/// <remarks>
/// Two things about the timing matter more than the list itself.
/// <para>
/// They are checked <b>only when a player would receive priority</b> (CR 704.3), never after each
/// individual change. The previous engine ran them after every mutation, which is why it could
/// kill a creature in the middle of a spell that was about to save it — CR 704.4 says state-based
/// actions pay no attention to what happens during a resolution.
/// </para>
/// <para>
/// They happen <b>simultaneously, as a single event</b>, and then the check repeats. Two
/// creatures that have each dealt the other lethal damage both die; neither dies first and
/// survives the other.
/// </para>
/// </remarks>
public static class StateBasedActions
{
    /// <summary>
    /// Everything that applies right now, as one batch. Empty when the game is stable.
    /// </summary>
    /// <remarks>
    /// Pure: it reads the state and reports what should happen. The caller applies the batch and
    /// asks again, until it comes back empty (CR 704.3).
    /// </remarks>
    public static IReadOnlyList<GameEvent> Check(GameState state, IAbilitySource abilities)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);

        var events = new List<GameEvent>();

        CheckPlayers(state, events);
        CheckCreatures(state, abilities, events);
        CheckPlaneswalkers(state, events);
        CheckBattles(state, abilities, events);
        CheckAuras(state, abilities, events);
        CheckEquipment(state, abilities, events);
        CheckTokens(state, events);
        CheckCounters(state, events);
        CheckSagas(state, abilities, events);
        CheckDungeons(state, events);

        return events;
    }

    /// <summary>A finished dungeon leaves the game, and its owner completes it (CR 309.6).</summary>
    /// <remarks>
    /// The same shape as the Saga rule above and for the same reason: the bottommost room's
    /// ability triggers on the marker arriving there, so at the moment the marker reaches it the
    /// ability has not resolved. A dungeon removed then is a dungeon whose last room never
    /// happened - which is exactly the mistake the Saga check exists to record, met a second time.
    /// <para>
    /// CR 309.7 says the player completes the dungeon <em>as</em> the card is removed, so the two
    /// events are emitted together: the completion is what "whenever you complete a dungeon"
    /// watches, and the removal is what makes the next venture start a new one.
    /// </para>
    /// </remarks>
    private static void CheckDungeons(GameState state, List<GameEvent> events)
    {
        foreach (var id in state.Command)
        {
            if (!state.TryGetObject(id, out var obj) || !Dungeons.IsDungeon(obj.Card))
                continue;

            if (!Dungeons.IsBottommost(obj.Card.Name, state.GetPlayer(obj.OwnerId).DungeonRoom))
                continue;

            var roomOnStack = state.Stack.Any(stacked =>
                state.TryGetObject(stacked, out var waiting)
                && waiting.Ability is { } ability
                && ability.SourceId == id);

            if (roomOnStack)
                continue;

            events.Add(new DungeonCompleted(obj.OwnerId, obj.Card.Name));
            events.Add(new ObjectCeasedToExist(id, Zone.Command));
        }
    }

    private static void CheckPlayers(GameState state, List<GameEvent> events)
    {
        foreach (var playerId in state.ActivePlayers())
        {
            var player = state.GetPlayer(playerId);

            // CR 704.5a.
            if (player.Life <= 0)
            {
                events.Add(new PlayerLost(playerId, "life total is 0 or less", "704.5a"));
                continue;
            }

            // CR 704.5b. The attempt is what loses the game, not the empty library — a player
            // with no cards left who is never asked to draw is still in the game.
            if (player.HasAttemptedDrawFromEmptyLibrary)
            {
                events.Add(new PlayerLost(playerId, "drew from an empty library", "704.5b"));
                continue;
            }

            // CR 704.5c.
            if (player.PoisonCounters >= 10)
            {
                events.Add(new PlayerLost(playerId, "ten or more poison counters", "704.5c"));
                continue;
            }

            // CR 903.10a: twenty-one combat damage from the same commander over the game.
            foreach (var (commander, taken) in player.CommanderDamage)
            {
                if (taken >= 21)
                {
                    events.Add(new PlayerLost(
                        playerId, "21 combat damage from one commander", "903.10a"));
                    break;
                }
            }
        }
    }

    private static void CheckCreatures(GameState state, IAbilitySource abilities, List<GameEvent> events)
    {
        foreach (var id in state.Battlefield)
        {
            var obj = state.GetObject(id);
            if (!Characteristics.IsCreature(state, abilities, obj))
                continue;

            var toughness = Characteristics.ToughnessOf(state, abilities, obj);
            if (toughness is null)
                continue;

            // CR 704.5f. Nothing can replace this one — a creature at 0 toughness is not
            // destroyed, it is put into the graveyard, so regeneration and indestructible miss it.
            if (toughness <= 0)
            {
                events.Add(new ObjectMoved(
                    id, ObjectId.New(), Zone.Battlefield, Zone.Graveyard,
                    obj.ControllerId, MoveCause.StateBasedAction));
                continue;
            }

            var indestructible =
                Characteristics.HasKeyword(state, abilities, obj, KeywordAbility.Indestructible);

            // CR 704.5g. Damage is compared with toughness here and nowhere else, which is what
            // lets a creature survive damage that was lethal a moment ago.
            var damage = obj.Permanent?.DamageMarked ?? 0;
            if (damage >= toughness && !indestructible)
            {
                // Destroyed, not merely moved: CR 704.5g destroys, and regeneration replaces
                // destruction. Zero toughness above is a different rule (CR 704.5f) and is not
                // destruction, which is why regeneration cannot save a creature from it.
                events.Add(new ObjectMoved(
                    id, ObjectId.New(), Zone.Battlefield, Zone.Graveyard,
                    obj.ControllerId, MoveCause.Destroy));
                continue;
            }

            // CR 704.5h. A separate action from lethal damage, and the reason one damage from a
            // deathtouch source kills a 6/6 without six damage ever being marked on it.
            if (obj.Permanent?.DealtDeathtouchDamage == true && !indestructible)
            {
                events.Add(new ObjectMoved(
                    id, ObjectId.New(), Zone.Battlefield, Zone.Graveyard,
                    obj.ControllerId, MoveCause.Destroy));
            }
        }
    }

    /// <summary>
    /// An Aura attached to nothing, or to something illegal, goes to the graveyard (CR 704.5m).
    /// </summary>
    /// <remarks>
    /// Equipment does not: it stays on the battlefield unattached (CR 301.5c), which is why this
    /// asks about the card's type rather than about whether it is attached. The commonest way to
    /// reach the state is the creature dying — the Aura is left holding nothing.
    /// <para>
    /// Which permanents are Auras is asked of the computed characteristics, not of the printed
    /// card (CR 613.1d). A Licid turns <em>itself</em> into an Aura in layer 4 while its card goes
    /// on saying Creature — Licid, and a printed-subtype reading exempted it from this rule
    /// entirely: its host died and it stayed on the battlefield, which is a card strictly better
    /// than the one printed.
    /// </para>
    /// </remarks>
    private static void CheckAuras(GameState state, IAbilitySource abilities, List<GameEvent> events)
    {
        foreach (var id in state.Battlefield)
        {
            var obj = state.GetObject(id);
            if (obj.Permanent is not { } permanent)
                continue;

            if (!BuriedWhenUnattached(state, abilities, obj))
                continue;

            // An Aura may be attached to a player rather than a permanent (CR 303.4a), and a
            // player does not leave the battlefield - so the only question for one of those is
            // whether that player is still in the game.
            //
            // "Still in the game" is not "still in the dictionary": a player who loses keeps
            // their seat in `Players` and is marked `HasLost`, which is how turn order skips
            // them. Asking `ContainsKey` was therefore always true, and an Aura enchanting an
            // eliminated player sat on the battlefield for the rest of the game. Only visible
            // at three seats or more, because at two the game ends with the player.
            if (permanent.AttachedToPlayer is { } enchanted)
            {
                if (state.Players.ContainsKey(enchanted)
                    && !state.GetPlayer(enchanted).HasLost)
                {
                    continue;
                }
            }
            else
            {
                var host = permanent.AttachedTo;
                var attachedToSomething = host is { } h
                    && state.TryGetObject(h, out var target)
                    && target.Zone == Zone.Battlefield;

                if (attachedToSomething)
                    continue;
            }

            events.Add(new ObjectMoved(
                id, ObjectId.New(), Zone.Battlefield, Zone.Graveyard,
                obj.ControllerId, MoveCause.StateBasedAction));
        }
    }

    /// <summary>
    /// Which permanents CR 704.5m buries when they have nothing to be attached to.
    /// </summary>
    /// <remarks>
    /// One predicate rather than a test in each of the two checks below, because they divide the
    /// battlefield between them and have to agree on the line: a permanent both counted as an Aura
    /// would be buried and unattached in the same batch, and the unattachment would land on an
    /// object that had already gone to the graveyard.
    /// <para>
    /// Bestow is the exception the rules themselves write (CR 702.103d): a bestowed permanent
    /// whose host leaves becomes unattached and <em>stays on the battlefield</em> as a creature
    /// again, which is the whole of what the keyword is worth. It is an Aura by its computed
    /// subtypes for exactly as long as it is attached, so nothing but the printed exception can
    /// tell it apart from a Licid at the moment the host dies — the two are in identical states
    /// and the rules send them to opposite places.
    /// </para>
    /// </remarks>
    private static bool BuriedWhenUnattached(
        GameState state, IAbilitySource abilities, GameObject obj) =>
        !obj.WasBestowed && Characteristics.IsAura(state, abilities, obj);

    /// <summary>
    /// Equipment whose creature has gone comes loose but stays put (CR 301.5c).
    /// </summary>
    /// <remarks>
    /// Everything CR 704.5m does not bury, which is what makes a bestowed permanent come loose
    /// here rather than there.
    /// </remarks>
    private static void CheckEquipment(
        GameState state, IAbilitySource abilities, List<GameEvent> events)
    {
        foreach (var id in state.Battlefield)
        {
            var obj = state.GetObject(id);
            if (obj.Permanent is not { AttachedTo: { } host })
                continue;

            if (BuriedWhenUnattached(state, abilities, obj))
                continue;

            var stillThere = state.TryGetObject(host, out var target)
                && target.Zone == Zone.Battlefield;

            if (!stillThere)
                events.Add(new PermanentAttached(id, null));
        }
    }

    private static void CheckPlaneswalkers(GameState state, List<GameEvent> events)
    {
        // CR 704.5i: a planeswalker with loyalty 0 is put into its owner's graveyard. It is not
        // destroyed, so indestructible does not save it — the same shape as a creature at zero
        // toughness (CR 704.5f).
        foreach (var id in state.Battlefield)
        {
            var obj = state.GetObject(id);
            if (!obj.Card.CardTypes.HasFlag(CardType.Planeswalker) || obj.Permanent is null)
                continue;

            if (obj.Permanent.Counters.GetValueOrDefault(CounterKinds.Loyalty) > 0)
                continue;

            events.Add(new ObjectMoved(
                id, ObjectId.New(), Zone.Battlefield, Zone.Graveyard,
                obj.ControllerId, MoveCause.StateBasedAction));
        }
    }

    /// <summary>
    /// A battle out of defense counters, or out of anyone to protect it, leaves (CR 704.5v–y).
    /// </summary>
    /// <remarks>
    /// The defense half is the planeswalker rule with the Saga's guard: a Siege at defense 0 is
    /// put into its owner's graveyard (CR 704.5v) <em>unless</em> it is the source of an ability
    /// that has triggered but not yet left the stack — its own defeat trigger fires on the last
    /// counter leaving (CR 310.12b), and a Siege buried before that trigger resolves is a Siege
    /// that never flips. Waiting triggers count as well as stacked ones, because state-based
    /// actions run before waiting triggers are put on the stack (CR 704.3, 603.3b).
    /// <para>
    /// The protector half only buries. Choosing a replacement protector when one can be chosen
    /// (CR 704.5x, 704.5y) is a question, and questions are asked by the settle sweep before the
    /// actions run — so a battle reaching here with no protector is one nobody eligible is left
    /// for, and CR 704.5x's own last sentence says where it goes.
    /// </para>
    /// </remarks>
    private static void CheckBattles(
        GameState state, IAbilitySource abilities, List<GameEvent> events)
    {
        foreach (var id in state.Battlefield)
        {
            var obj = state.GetObject(id);
            if (!obj.Card.CardTypes.HasFlag(CardType.Battle) || obj.Permanent is null)
                continue;

            if (obj.Permanent.Counters.GetValueOrDefault(CounterKinds.Defense) <= 0)
            {
                // CR 704.5v for a Siege; CR 704.5w, without the guard, for anything else. The
                // guard covers every ability of the battle rather than naming the defeat
                // trigger, which is the rule's own wording.
                var pending = state.PendingTriggers.Any(t => t.SourceId == id)
                    || state.Stack.Any(stacked =>
                        state.TryGetObject(stacked, out var waiting)
                        && waiting.Ability is { } ability
                        && ability.SourceId == id);

                var siege = obj.Card.Subtypes.Contains("Siege", StringComparer.Ordinal);

                if (!siege || !pending)
                {
                    events.Add(new ObjectMoved(
                        id, ObjectId.New(), Zone.Battlefield, Zone.Graveyard,
                        obj.ControllerId, MoveCause.StateBasedAction));
                }

                continue;
            }

            // CR 704.5x, 704.5y: a battle whose designated protector is gone, or was never
            // chosen, and for whom no eligible player remains. The settle sweep already asked
            // whenever somebody could be chosen, so reaching here with candidates would mean the
            // question is on its way — being attacked is the one state that defers even that
            // (CR 704.5x).
            var protector = obj.Permanent.ProtectorId;
            var protectorFine = protector is { } chosen
                && !state.GetPlayer(chosen).HasLost
                && EligibleProtectors(state, abilities, obj).Contains(chosen);

            if (protectorFine)
                continue;

            var underAttack = state.Combat.Attackers.Values.Any(at => at.Planeswalker == id);
            if (underAttack)
                continue;

            if (!EligibleProtectors(state, abilities, obj).Any())
            {
                events.Add(new ObjectMoved(
                    id, ObjectId.New(), Zone.Battlefield, Zone.Graveyard,
                    obj.ControllerId, MoveCause.StateBasedAction));
            }
        }
    }

    /// <summary>
    /// Who may be designated a battle's protector (CR 310.9a).
    /// </summary>
    /// <remarks>
    /// Determined by its battle type: only an opponent of a Siege's controller (CR 310.12a), and
    /// only the controller for a battle with no battle type. The controller is read computed
    /// rather than stored because control is layer 2 (CR 613.1b) — a stolen Siege must be
    /// protected by an opponent of whoever holds it now.
    /// </remarks>
    internal static IEnumerable<Guid> EligibleProtectors(
        GameState state, IAbilitySource abilities, GameObject battle)
    {
        var controller = Characteristics.Of(state, abilities, battle).ControllerId;

        if (!battle.Card.Subtypes.Contains("Siege", StringComparer.Ordinal))
            return state.GetPlayer(controller).HasLost ? [] : [controller];

        return state.TurnOrder.Where(
            player => player != controller && !state.GetPlayer(player).HasLost);
    }

    private static void CheckSagas(
        GameState state, IAbilitySource abilities, List<GameEvent> events)
    {
        // CR 714.4: once a Saga's lore counters reach its final chapter number, and it is not
        // the source of a chapter ability still on the stack, its controller sacrifices it.
        //
        // The second half is the whole of why this is not a one-line check. The final chapter
        // triggers on the same counter that finishes the Saga, so at the moment the count is
        // reached the ability has not resolved yet - and a Saga sacrificed before its last
        // chapter resolves is a Saga that never does the thing it was played for.
        foreach (var id in state.Battlefield)
        {
            var obj = state.GetObject(id);
            if (obj.Permanent is null)
                continue;

            // CR 707.2a: a Saga that is a copy of another Saga has the copied card's chapter
            // abilities, and CR 714.4's final chapter number is counted from those. Reading the
            // printed card counted the wrong Saga's chapters, which sacrifices the permanent at
            // the wrong lore count in both directions.
            var card = Characteristics.CardOf(state, abilities, obj);

            var chapters = abilities.TriggersOf(card)
                .Where(t => t.Chapter is not null)
                .Select(t => t.Chapter!.Value)
                .ToList();

            // CR 714.2d: a Saga with no chapter abilities has a final chapter number of 0, and
            // this rule does not touch it. Reading that as "0 >= 0, sacrifice it" would destroy
            // every Saga whose chapters went unread the moment it arrived.
            if (chapters.Count == 0)
                continue;

            if (obj.Permanent.Counters.GetValueOrDefault(CounterKinds.Lore) < chapters.Max())
                continue;

            var onStack = state.Stack.Any(stacked =>
                state.TryGetObject(stacked, out var waiting)
                && waiting.Ability is { } ability
                && ability.SourceId == id

                // The Saga's own card, not the one the ability on the stack carries: a chapter
                // of a copied Saga is looked up on the card the permanent is a copy of.
                && abilities.TriggersOf(card)
                    .Any(t => t.Id == ability.AbilityId && t.Chapter is not null));

            if (onStack)
                continue;

            events.Add(new ObjectMoved(
                id, ObjectId.New(), Zone.Battlefield, Zone.Graveyard,
                obj.ControllerId, MoveCause.Sacrifice));
        }
    }

    private static void CheckTokens(GameState state, List<GameEvent> events)
    {
        // CR 704.5d: a token anywhere but the battlefield ceases to exist. It gets there first —
        // it is put into a graveyard and then stops existing — so this runs on the next check.
        foreach (var (id, obj) in state.Objects)
        {
            // An ability on the stack is not the permanent it came from, even though it carries
            // that permanent's card so its text can be shown (CR 113.7a). Without this test, a
            // token's own dies-trigger was destroyed by this rule the instant it went on the
            // stack — so no token with a triggered ability had ever resolved one.
            if (obj.Ability is not null)
                continue;

            if (obj.Zone != Zone.Battlefield && obj.Card.CardTypes.HasFlag(CardType.Token))
                events.Add(new ObjectCeasedToExist(id, obj.Zone));
        }
    }

    private static void CheckCounters(GameState state, List<GameEvent> events)
    {
        // CR 704.5q: +1/+1 and -1/-1 counters annihilate in pairs.
        foreach (var id in state.Battlefield)
        {
            var counters = state.GetObject(id).Permanent?.Counters;
            if (counters is null)
                continue;

            var plus = counters.GetValueOrDefault(CounterKinds.PlusOnePlusOne);
            var minus = counters.GetValueOrDefault(CounterKinds.MinusOneMinusOne);
            var pairs = Math.Min(plus, minus);
            if (pairs <= 0)
                continue;

            events.Add(new CountersChanged(id, CounterKinds.PlusOnePlusOne, -pairs));
            events.Add(new CountersChanged(id, CounterKinds.MinusOneMinusOne, -pairs));
        }
    }
}
