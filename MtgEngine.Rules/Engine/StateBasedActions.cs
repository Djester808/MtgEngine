using MtgEngine.Domain.Enums;
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
    public static IReadOnlyList<GameEvent> Check(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var events = new List<GameEvent>();

        CheckPlayers(state, events);
        CheckCreatures(state, events);
        CheckTokens(state, events);
        CheckCounters(state, events);
        CheckLegendRule(state, events);

        return events;
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
                events.Add(new PlayerLost(playerId, "ten or more poison counters", "704.5c"));
        }
    }

    private static void CheckCreatures(GameState state, List<GameEvent> events)
    {
        foreach (var id in state.Battlefield)
        {
            var obj = state.GetObject(id);
            if (!Characteristics.IsCreature(state, obj))
                continue;

            var toughness = Characteristics.ToughnessOf(state, obj);
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

            // CR 704.5g. Damage is compared with toughness here and nowhere else, which is what
            // lets a creature survive damage that was lethal a moment ago.
            var damage = obj.Permanent?.DamageMarked ?? 0;
            if (damage >= toughness
                && !Characteristics.HasKeyword(state, obj, KeywordAbility.Indestructible))
            {
                events.Add(new ObjectMoved(
                    id, ObjectId.New(), Zone.Battlefield, Zone.Graveyard,
                    obj.ControllerId, MoveCause.StateBasedAction));
            }
        }
    }

    private static void CheckTokens(GameState state, List<GameEvent> events)
    {
        // CR 704.5d: a token anywhere but the battlefield ceases to exist. It gets there first —
        // it is put into a graveyard and then stops existing — so this runs on the next check.
        foreach (var (id, obj) in state.Objects)
        {
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

    private static void CheckLegendRule(GameState state, List<GameEvent> events)
    {
        // CR 704.5j. The rules let the controller choose which one to keep; with nothing to
        // distinguish them the engine keeps the one that has been there longest, which is the
        // only choice that does not depend on dictionary order. Making it the player's choice
        // needs the choice machinery that arrives with the effect system.
        var legends = state.Battlefield
            .Select(state.GetObject)
            .Where(o => o.Card.Supertypes.Contains("Legendary", StringComparer.OrdinalIgnoreCase))
            .GroupBy(o => (o.ControllerId, o.Card.Name), StringTupleComparer.Instance);

        foreach (var group in legends)
        {
            var duplicates = group.OrderBy(o => o.Timestamp).Skip(1).ToList();
            foreach (var doomed in duplicates)
            {
                events.Add(new ObjectMoved(
                    doomed.Id, ObjectId.New(), Zone.Battlefield, Zone.Graveyard,
                    doomed.ControllerId, MoveCause.StateBasedAction));
            }
        }
    }

    /// <summary>Groups legendary permanents by controller and name, comparing names ordinally.</summary>
    private sealed class StringTupleComparer : IEqualityComparer<(Guid, string)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((Guid, string) x, (Guid, string) y) =>
            x.Item1 == y.Item1 && string.Equals(x.Item2, y.Item2, StringComparison.Ordinal);

        public int GetHashCode((Guid, string) obj) =>
            HashCode.Combine(obj.Item1, obj.Item2);
    }
}
