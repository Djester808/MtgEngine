using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Events;

// The game's own lifecycle, split out of GameEvent.cs when that file reached the 500-line
// ceiling tools/check-file-size.js enforces: the seeded generator advancing, a player leaving
// the game, the game ending, and a manual action on an honour-system object.
/// <summary>The randomness was used: the state's counter advanced. The consuming event records what came out.</summary>
public sealed record RngAdvanced(RngState To) : GameEvent
{
    public override GameState Apply(GameState state) => state with { Rng = To };
}

[RuleRef("104.3")]
public sealed record PlayerLost(PlayerId Player, string Reason) : GameEvent
{
    /// <summary>
    /// The player leaves, and CR 800.4a empties the board of them. Nothing here ran at all: a
    /// departed player's permanents stayed on the battlefield for the rest of the game AND were
    /// skipped by every state-based action (704.5 defers object removal to this rule), so a
    /// creature with no toughness left could never die.
    ///
    /// The rule is four steps and they are written in an order, because each reads what the one
    /// before it left: the objects they own leave, then the control they were GIVEN ends, then
    /// what they control on the stack that is not a card ceases, then whatever they still control
    /// is exiled. Only the first was built, which is why a permanent they controlled but did not
    /// own stayed on the battlefield under a seat that is no longer a player.
    ///
    /// This is in the fold, not at the three sites that emit PlayerLost, so no site can forget.
    /// </summary>
    [RuleRef("800.4a")]
    public override GameState Apply(GameState state)
    {
        var p = state.Player(Player);
        var next = state with
        {
            Players = state.Players.SetItem(Player.Value, p with { HasLost = true }),
            LibraryInspections = state.LibraryInspections.RemoveRange(state.LibraryInspections
                .Where(pair => pair.Value.Viewer == Player || pair.Value.Owner == Player)
                .Select(pair => pair.Key)),
            OpeningHands = state.OpeningHands is { } opening
                ? opening with { Seats = opening.Seats.RemoveAll(seat => seat.Player == Player) }
                : null,
            Pending = state.Pending is { } pending && pending.Player == Player
                && pending.Kind is ChoiceKinds.Mulligan or ChoiceKinds.MulliganBottom
                    ? null
                    : state.Pending,
        };

        // 1. "all objects owned by that player leave the game".
        foreach (var owned in Ordered(state.Objects.Where(pair => pair.Value.Owner == Player)))
        {
            next = new ObjectCeasedToExist(owned).Apply(next);
        }

        // 2. "and any effects which give that player control of any objects or players end". The
        // effect ends, so what it moved goes back to whoever had it by default — Act of Treason's
        // creature returns to its owner rather than being exiled with the caster.
        next = next with
        {
            ActiveEffects = next.ActiveEffects.RemoveAll(effect => effect.Effect is ControlBy && effect.Controller == Player),
        };

        // 3. "Then, if that player controlled any objects on the stack not represented by cards,
        // those objects cease to exist" — an ability waiting to resolve, or a copy of a spell
        // (CR 707.10). A card they merely control goes to step 4 and is exiled instead.
        foreach (var id in next.In(ZoneKey.Shared(Zone.Stack)).ToList())
        {
            if (next.Objects.TryGetValue(id, out var waiting)
                && Controls(next, waiting)
                && waiting.Stack is { AbilityIndex: not null } or { IsCopy: true })
            {
                next = new ObjectCeasedToExist(id).Apply(next);
            }
        }

        // 4. "Then, if there are any objects still controlled by that player, those objects are
        // exiled" — the rule's own Bribery example: a creature put onto the battlefield under the
        // departed player's control, owned by somebody still playing, is exiled rather than left.
        // The ids are taken in order so a replayed log mints the new objects the same way round.
        foreach (var id in Ordered(next.Objects.Where(pair => Controls(next, pair.Value))))
        {
            if (!next.Objects.TryGetValue(id, out var held))
                continue;

            // CR 400.7: what arrives in exile is a new object, and everything not printed on the
            // card is dropped — the same rebirth ZoneSystem performs for every other move.
            var exiled = ZoneSystem.Reborn(next, held, ZoneKey.Shared(Zone.Exile),
                tapped: false, controller: held.Owner, faceIndex: null, faceDown: false);

            next = new ObjectMoved(id, exiled, -1, held).Apply(next);
        }

        // CR 800.4a: departure hands existing priority to the next surviving seat.
        // Preserve the other players' passes; leaving is not a new priority round.
        return next with
        {
            Priority = next.Priority with
            {
                Holder = next.Priority.Holder == Player ? PlayerTurnOrder.NextLiving(next, Player) : next.Priority.Holder,
                PassedInSuccession = next.Priority.PassedInSuccession.Remove(Player),
            },
        };
    }

    /// <summary>The ids of these objects, low to high, so a fold is the same on every replay.</summary>
    private static IReadOnlyList<ObjectId> Ordered(IEnumerable<KeyValuePair<ObjectId, GameObject>> objects) =>
        [.. objects.Select(pair => pair.Key).OrderBy(id => id.Value)];

    /// <summary>
    /// Whether the departed player controls this object, CR 613.1b read in the fold: the object's
    /// own controller unless a control-changing effect that applies to it says otherwise, latest
    /// timestamp winning. Step 2 has already dropped the effects that gave THEM control, so what
    /// is left here is control they hold by default — which is what step 4 asks about.
    /// </summary>
    [RuleRef("613.1b")]
    private bool Controls(GameState state, GameObject obj)
    {
        var controller = obj.InitialController;
        foreach (var effect in state.ActiveEffects
            .Where(e => e.Effect is ControlBy && e.AppliesTo.Contains(obj.Id))
            .OrderBy(e => e.Timestamp))
        {
            controller = effect.Controller;
        }

        return controller == Player;
    }
}

[RuleRef("104")]
public sealed record GameEnded(GameOutcome Outcome) : GameEvent
{
    public override GameState Apply(GameState state) => state with
    {
        Outcome = Outcome,
        Priority = PriorityState.None,
        LibraryInspections = state.LibraryInspections.Clear(),
    };
}

/// <summary>
/// A player did something by hand on an honour-system object. The actor is recorded so the
/// log shows who did what; the engine applied no rule to it.
/// </summary>
public sealed record ManualActionTaken(PlayerId Actor, string Action, ObjectId? Object, string Detail) : GameEvent
{
    public Guid? RequestId { get; init; }

    public override GameState Apply(GameState state) => RequestId is { } request
        ? state with { ManualRequests = state.ManualRequests.Add(new ManualRequest(Actor, request)) }
        : state;
}
