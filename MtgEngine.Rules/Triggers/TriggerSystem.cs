using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using Ev = MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Triggers;

/// <summary>
/// Triggered abilities, CR 603. Every event in the batch is offered to every ability that
/// functions where its object is (CR 603.6d); the ones that trigger are put on the stack the next
/// time a player would receive priority, in APNAP order, CR 603.3b. Nothing here decides what a
/// trigger does: it makes the stack object and the interpreter resolves it.
/// </summary>
[RuleRef("603")]
public sealed class TriggerSystem(IQuery query, ICharacteristics characteristics) : ITriggerSystem
{
    private readonly IQuery _query = query;
    private readonly ICharacteristics _layers = characteristics;
    private readonly TriggerMatching _matching = new(query, characteristics);

    public string Name => "triggers";

    /// <summary>The kind a pending choice carries when a player has more than one trigger to order, CR 603.3b.</summary>
    public const string OrderChoice = "trigger-order";

    public Outcome Collect(GameState state, IReadOnlyList<GameEvent> since)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(since);
        if (since.Count == 0)
            return Outcome.Unchanged(state);

        var fired = ImmutableList.CreateBuilder<Firing>();
        for (var i = 0; i < since.Count; i++)
        {
            var lookback = LookBack(state, since[i]);
            var window = new TriggerWindow(lookback, since, i);
            fired.AddRange(FromObjects(lookback, since[i], window));
            fired.AddRange(FromDelayed(lookback, since[i], window));
        }

        var kept = OncePerTurn(state, fired.ToImmutable());
        return kept.Count == 0 ? Outcome.Unchanged(state) : PutOnStack(state, Apnap(state, kept));
    }

    /// <summary>
    /// "This ability triggers only once each turn", CR 603.2h: an ability so worded triggers only
    /// while its controller has not already taken the action this turn. The rule is about
    /// triggering, not about resolving, so a trigger that is on the stack and has not resolved yet
    /// counts; and nothing in the state records a firing on its own. So a firing is already spent
    /// when either the turn's resolution tally has counted one for that ability
    /// (<see cref="TurnHistory.AbilitiesResolvedThisTurn"/>, keyed the way
    /// <c>StackObjectResolved</c> folds it) or a stack object of that source and index is waiting.
    /// Within one batch of events the ability fires for the first event only, because the stack
    /// object for it does not exist until the whole batch has been collected.
    ///
    /// The one firing this misses is a trigger that fired and then left the stack without
    /// resolving — countered, or its source's ability removed. Counting that would need the
    /// history to record firings as well as resolutions, which is a field on a record this
    /// subsystem does not own; the two readings it does have are exact for every other case.
    /// </summary>
    [RuleRef("603.2h")]
    private static ImmutableList<Firing> OncePerTurn(GameState state, ImmutableList<Firing> fired)
    {
        var kept = ImmutableList.CreateBuilder<Firing>();
        var already = new HashSet<(ObjectId? Source, int Index)>();
        foreach (var firing in fired)
        {
            if (firing.Ability.OncePerTurn
                && (!already.Add((firing.Source, firing.AbilityIndex)) || Spent(state, firing.Source, firing.AbilityIndex)))
            {
                continue;
            }

            kept.Add(firing);
        }

        return kept.ToImmutable();
    }

    /// <summary>
    /// Whether this ability has already triggered this turn: it resolved (CR 608.2, the tally the
    /// resolution folds) or it is on the stack waiting to (CR 603.3). An ability with no source
    /// object — a delayed trigger, CR 603.7 — has no key in either, and is spent by its own
    /// <see cref="DelayedTriggerState"/> instead.
    /// </summary>
    [RuleRef("603.2h")]
    private static bool Spent(GameState state, ObjectId? source, int index)
    {
        if (source is not { } id)
            return false;
        if (state.History.AbilitiesResolvedThisTurn.GetValueOrDefault((id, index)) > 0)
            return true;

        foreach (var onStack in state.In(ZoneKey.Shared(Zone.Stack)))
        {
            if (state.Objects.TryGetValue(onStack, out var obj)
                && obj.Stack is { } entry
                && (entry.Source ?? obj.Id) == id
                && (entry.AbilityIndex ?? -1) == index)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What triggered, and everything needed to make its stack object.</summary>
    private sealed record Firing(ObjectId? Source, int AbilityIndex, PlayerId Controller, TriggeredAbility Ability, TriggerSubject Subject, int? Delayed);

    /// <summary>
    /// CR 603.10: a leaves-the-battlefield trigger looks back at the game as it was before the
    /// object left. The batch says what left — <see cref="TurnHistory.LeftBattlefield"/> is folded
    /// by the move itself — so the object is put back on the battlefield for the length of the
    /// match and nothing else about the state is touched.
    ///
    /// The object put back is the one the move carries: <see cref="Ev.ObjectMoved.Departed"/> is
    /// the permanent as it last existed, with its counters, its damage and the player who
    /// controlled it. The new object in the graveyard is a different object (CR 400.7) that kept
    /// none of that, so rebuilding from it answers "a 2/2 its owner controlled" where the rule
    /// asks about the 3/3 someone had stolen. That rebuild is kept only for a move that carries
    /// no departed object, which is a fake in a test rather than anything the zone system emits.
    /// </summary>
    [RuleRef("603.10")]
    private static GameState LookBack(GameState state, GameEvent happened)
    {
        if (happened is not Ev.ObjectMoved moved || !state.History.LeftBattlefield.Any(o => o.Id == moved.From))
            return state;
        if (state.Objects.ContainsKey(moved.From))
            return state;

        var battlefield = ZoneKey.Shared(Zone.Battlefield);
        var departed = (moved.Departed ?? moved.To) with { Id = moved.From, Zone = battlefield, Stack = null };
        return state with
        {
            Objects = state.Objects.SetItem(moved.From, departed),
            Zones = state.Zones.SetItem(battlefield, state.In(battlefield).Add(moved.From)),
        };
    }

    /// <summary>CR 603.6d: an ability triggers only while its object is in the zone the ability functions in.</summary>
    [RuleRef("603.6d")]
    private IEnumerable<Firing> FromObjects(GameState state, GameEvent happened, TriggerWindow window)
    {
        foreach (var id in state.Objects.Keys.OrderBy(k => k.Value).ToList())
        {
            if (!state.Objects.TryGetValue(id, out var obj))
                continue;
            var abilities = _layers.Of(state, id).Abilities;
            for (var index = 0; index < abilities.Count; index++)
            {
                if (abilities[index] is not TriggeredAbility ability || ability.Where != obj.Zone.Zone)
                    continue;
                var controller = _layers.ControllerOf(state, id);
                foreach (var subject in Fire(state, ability, id, index, controller, happened, window, delayed: null))
                    yield return subject;
            }
        }
    }

    /// <summary>CR 603.7: a delayed trigger waits for its event, then fires the same way and is spent if it fires once.</summary>
    [RuleRef("603.7")]
    private IEnumerable<Firing> FromDelayed(GameState state, GameEvent happened, TriggerWindow window)
    {
        foreach (var delayed in state.DelayedTriggers)
        {
            var ability = new TriggeredAbility(delayed.When, delayed.Do);
            foreach (var firing in Fire(state, ability, delayed.Source, -1, delayed.Controller, happened, window, delayed.Once ? delayed.Id : null))
                yield return firing;
        }
    }

    private IEnumerable<Firing> Fire(
        GameState state,
        TriggeredAbility ability,
        ObjectId? source,
        int index,
        PlayerId controller,
        GameEvent happened,
        TriggerWindow window,
        int? delayed)
    {
        var context = Context(source, controller, TriggerSubject.Nothing);
        var subjects = _matching.Match(ability.When, happened, context, window);
        foreach (var subject in subjects)
        {
            // CR 603.4: the intervening "if" is checked as the ability would go on the stack, and
            // the ability simply does not go there when it is false.
            if (ability.If is { } condition && !_query.Holds(state, condition, Context(source, controller, subject)))
                continue;
            yield return new Firing(source, index, controller, ability, subject, delayed);
        }
    }

    private static EffectContext Context(ObjectId? source, PlayerId controller, TriggerSubject subject) => new(
        source,
        controller,
        [],
        [],
        null,
        ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem(Triggering, subject.Objects),
        ImmutableDictionary<string, string>.Empty,
        ImmutableDictionary<string, int>.Empty,
        subject.Objects,
        subject.Player);

    /// <summary>The key the triggering objects are remembered under, so "that creature" finds them.</summary>
    public const string Triggering = "triggering";

    /// <summary>
    /// CR 603.3b: the active player's triggers go on the stack first, then each other player's in
    /// turn order. Within one player the order is that player's to choose, so the collection order
    /// stands and a choice is raised when there is actually something to decide.
    /// </summary>
    [RuleRef("603.3b")]
    private static ImmutableList<Firing> Apnap(GameState state, ImmutableList<Firing> fired)
    {
        var seats = state.Players.Count;
        var active = state.Turn.ActivePlayer.Value;
        int Order(PlayerId player) => ((player.Value - active) % seats + seats) % seats;
        return [.. fired.OrderBy(f => Order(f.Controller))];
    }

    private Outcome PutOnStack(GameState state, ImmutableList<Firing> fired)
    {
        var events = ImmutableList.CreateBuilder<GameEvent>();
        var stacked = new List<(PlayerId Controller, ObjectId Id)>();
        foreach (var firing in fired)
        {
            var created = Create(state, firing);
            events.Add(created);
            state = created.Apply(state);
            var id = created.Object.Id;
            var announced = new TriggerFired(id, firing.Source, firing.AbilityIndex, firing.Controller, firing.Subject.Objects, firing.Subject.Player, firing.Delayed);
            events.Add(announced);
            state = announced.Apply(state);
            stacked.Add((firing.Controller, id));
        }

        if (Ordering(state, stacked) is { } question)
        {
            events.Add(question);
            state = question.Apply(state);
        }

        return new Outcome(state, events.ToImmutable());
    }

    /// <summary>
    /// CR 602.2a shapes the object: an ability on the stack is not a card, it keeps its source's
    /// definition so its text can be read, and its <see cref="StackEntry.AbilityIndex"/> says which
    /// ability it is. A delayed trigger has no source object on the battlefield, so it is created
    /// from the state's own bookkeeping rather than from a permanent.
    /// </summary>
    [RuleRef("603.3")]
    private static Ev.ObjectCreated Create(GameState state, Firing firing)
    {
        // The stack object carries what it triggered on, so the interpreter builds its context
        // from the object alone (CR 603.3d) rather than from the log.
        var entry = new StackEntry(
            firing.Source,
            firing.AbilityIndex,
            [],
            [],
            [],
            null,
            IsCopy: false,
            Remembered: ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem(Triggering, firing.Subject.Objects),
            RememberedPlayers: firing.Subject.Player is { } player
                ? ImmutableDictionary<string, ImmutableList<PlayerId>>.Empty.SetItem(Triggering, [player])
                : null,
            Triggering: firing.Subject.Objects);
        if (firing.Source is { } source && state.Objects.TryGetValue(source, out var obj))
            return StackZone.CreateAbilityOnStack(state, obj, firing.Controller, entry);

        var free = new GameObject(
            new ObjectId(state.NextObjectId),
            EmptyDefinition,
            0,
            firing.Controller,
            firing.Controller,
            StackZone.Key,
            state.NextTimestamp)
        {
            Stack = entry,
            SummoningSick = false,
        };
        return new Ev.ObjectCreated(free, -1);
    }

    /// <summary>A delayed trigger's stack object has no card behind it, CR 113.7a; the ability is the whole of it.</summary>
    private static CardDefinition EmptyDefinition { get; } = new(
        string.Empty,
        string.Empty,
        Layout.Normal,
        [new Face(string.Empty, FaceRole.Main, new TypeLine(CardType.None))]);

    /// <summary>
    /// CR 603.3b: only a player with more than one trigger has an order to choose. The abilities
    /// are already on the stack in the order they were collected, so the answer reorders the stack
    /// rather than deciding whether anything happens.
    /// </summary>
    [RuleRef("603.3b")]
    private static Ev.ChoiceRequested? Ordering(GameState state, List<(PlayerId Controller, ObjectId Id)> stacked)
    {
        var many = stacked.GroupBy(s => s.Controller).FirstOrDefault(g => g.Count() > 1);
        if (many is null)
            return null;

        var options = many.Select(s => new ChoiceOption(
            s.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            state.Objects[s.Id].Definition.Name)).ToImmutableList();
        var choice = new PendingChoice(
            new ChoiceId(state.NextChoiceId),
            many.Key,
            OrderChoice,
            "Order your triggered abilities on the stack.",
            options,
            options.Count,
            options.Count,
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty));
        return new Ev.ChoiceRequested(choice);
    }
}
