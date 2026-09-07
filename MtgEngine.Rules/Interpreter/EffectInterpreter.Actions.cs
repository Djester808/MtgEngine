using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// The keyword actions of CR 701 that move objects about: destroy, exile, sacrifice, counter,
/// discard, mill, tap and the searches. Each one that the rules name as a distinct action emits
/// an <see cref="ObjectLeftBy"/> beside the zone change, so a "whenever a creature dies" trigger
/// can tell a sacrifice from a destruction.
/// </summary>
public sealed partial class EffectInterpreter
{
    private EffectRun? Action(EffectRun run, Effect effect)
    {
        switch (effect)
        {
            // CR 400: the one zone-change verb. The object that arrives is a new object, CR 400.7.
            case Cards.Move move:
                {
                    var arrived = new List<ObjectId>();
                    foreach (var id in Objects(run, move.What))
                    {
                        var controller = move.UnderControlOf is null ? (PlayerId?)null : FirstPlayer(run, move.UnderControlOf);
                        (run, var landed) = Relocate(run, id, move.To, move.Where, move.Tapped, controller);
                        if (landed is { } now)
                            arrived.Add(now);
                    }

                    return Recall(run, move.RememberAs, arrived);
                }

            // CR 701.8b: a permanent with indestructible is not destroyed.
            case Destroy destroy:
                return Leave(run, Objects(run, destroy.What), "destroyed", Zone.Graveyard, skipIndestructible: true);

            case Cards.Exile exile:
                {
                    var ids = Objects(run, exile.What);
                    var before = run.State;
                    run = Leave(run, ids, "exiled", Zone.Exile, skipIndestructible: false);
                    return Recall(run, exile.RememberAs, Landed(before, run));
                }

            // CR 701.21a: sacrificing is the controller's own action and cannot be replaced.
            case Sacrifice sacrifice:
                {
                    if (sacrifice.What is { } named)
                        return Leave(run, Objects(run, named), "sacrificed", Zone.Graveyard, skipIndestructible: false);

                    // CR 701.21a: the player has now chosen, so the permanents they named leave.
                    if (Answered(run) is { } given)
                        return Leave(run, Named(run.State, given), "sacrificed", Zone.Graveyard, skipIndestructible: false);

                    var chooser = FirstPlayer(run, sacrifice.Who);
                    var options = OptionsFor(run.State, Controlled(run.State, chooser, sacrifice.Choice, run.Context));
                    var count = Math.Max(1, Number(run, sacrifice.N));
                    return Ask(run, chooser, "sacrifice", "Choose what to sacrifice.", options, count, count);
                }

            // CR 701.6a: a countered spell goes to its owner's graveyard unless told otherwise.
            case Cards.Counter counter:
                return Leave(run, Objects(run, counter.What), "countered", counter.PutInto ?? Zone.Graveyard, skipIndestructible: false);

            case Cards.Tap tap:
                {
                    foreach (var id in Objects(run, tap.What))
                        run = Emit(run, new ObjectChanged(id, Tapped: true));
                    return run;
                }

            case Cards.Untap untap:
                {
                    foreach (var id in Objects(run, untap.What))
                        run = Emit(run, new ObjectChanged(id, Tapped: false));
                    return run;
                }

            // CR 701.7: the token is created by the zone system, which knows how an object enters.
            case Cards.CreateToken token:
                {
                    // CR 707.2: a token that is a copy of something takes its copiable values.
                    if (token.CopyOf is { } copied)
                        return CopyToken(run, token, copied);

                    var created = new List<ObjectId>();
                    var count = Math.Max(0, Number(run, token.N));
                    foreach (var player in PlayersOrYou(run, token.Who))
                    {
                        for (var i = 0; i < count; i++)
                        {
                            var before = run.State;
                            run = Merge(run, zones.CreateToken(run.State, token.Token, player, token.Tapped, token.Attacking));
                            created.AddRange(run.State.Objects.Keys.Where(id => !before.Objects.ContainsKey(id)));
                        }
                    }

                    return Recall(run, token.RememberAs, created);
                }

            // CR 701.9a: the player chooses which card, unless the whole hand goes.
            case Cards.Discard discard:
                {
                    var discarding = PlayersOrYou(run, discard.Who);
                    var next = 0;

                    // CR 701.9a: the cards this player chose are discarded, and the players after
                    // them are asked in their turn.
                    if (Answered(run) is { } discarded && run.Answer is { } answer)
                    {
                        run = Leave(run, Named(run.State, discarded), "discarded", Zone.Graveyard, skipIndestructible: false);
                        next = discarding.IndexOf(answer.Player) + 1;
                    }

                    foreach (var player in discarding.Skip(next))
                    {
                        var hand = run.State.In(ZoneKey.Of(Zone.Hand, player));
                        if (discard.WholeHand)
                        {
                            run = Leave(run, hand, "discarded", Zone.Graveyard, skipIndestructible: false);
                            continue;
                        }

                        var count = Math.Min(hand.Count, Math.Max(0, Number(run, discard.N)));
                        if (count == 0)
                            continue;
                        return Ask(run, player, "discard", "Choose what to discard.", OptionsFor(run.State, hand), count, count);
                    }

                    return run;
                }

            // CR 701.17a: milling puts the top cards into the graveyard, in order.
            case Cards.Mill mill:
                {
                    var before = run.State;
                    foreach (var player in PlayersOrYou(run, mill.Who))
                    {
                        var count = Math.Max(0, Number(run, mill.N));
                        var top = run.State.In(ZoneKey.Of(Zone.Library, player)).Take(count).ToList();
                        foreach (var id in top)
                            (run, _) = Relocate(run, id, Zone.Graveyard, Placement.Default, tapped: false, controller: null);
                    }

                    return Recall(run, mill.RememberAs, Landed(before, run));
                }

            // CR 701.22a and 701.25a: the player looks and then decides, which is a question.
            case Cards.Scry scry:
                return Answered(run) is { } scried
                    ? Sorted(run, Named(run.State, scried), Zone.Library, Placement.Bottom)
                    : Look(run, scry.Who, scry.N, "scry", "Put any number on the bottom.");

            case Cards.Surveil surveil:
                return Answered(run) is { } surveilled
                    ? Sorted(run, Named(run.State, surveilled), Zone.Graveyard, Placement.Default)
                    : Look(run, surveil.Who, surveil.N, "surveil", "Put any number into your graveyard.");

            // CR 701.23a: searching shows the player what is there and asks which cards to take.
            case Cards.Search search:
                {
                    var searcher = FirstPlayer(run, search.Who);

                    // CR 701.23a: the cards the player found go where the effect says, and then
                    // CR 701.23c shuffles the library whether anything was found or not.
                    if (Answered(run) is { } found)
                        return Found(run, search, searcher, Named(run.State, found));

                    var zone = ZoneSystem.KeyFor(search.From, searcher);
                    var matching = run.State.In(zone).Where(id => query.Matches(run.State, search.For, id, run.Context));
                    var most = Math.Max(1, Number(run, search.UpTo));
                    return Ask(run, searcher, "search", "Search for a card.", OptionsFor(run.State, matching), search.UpTo is null ? 0 : 0, most);
                }

            case Cards.Shuffle shuffle:
                {
                    foreach (var player in PlayersOrYou(run, shuffle.Who))
                        run = Merge(run, zones.Shuffle(run.State, player));
                    return run;
                }

            default:
                return null;
        }
    }

    /// <summary>
    /// The named-action shape: the rules say the object left by this action, then it changes
    /// zones. Both are events, so a trigger can watch either.
    /// </summary>
    private EffectRun Leave(EffectRun run, IEnumerable<ObjectId> ids, string verb, Zone to, bool skipIndestructible)
    {
        foreach (var id in ids)
        {
            if (!run.State.Objects.ContainsKey(id))
                continue;
            if (skipIndestructible && Has(run.State, id, KeywordKind.Indestructible))
                continue;
            run = Emit(run, new ObjectLeftBy(id, verb, run.Context.Source));
            (run, _) = Relocate(run, id, to, Placement.Default, tapped: false, controller: null);
        }

        return run;
    }

    /// <summary>One zone change through the zone system, and the id the object came back with, CR 400.7.</summary>
    private (EffectRun Run, ObjectId? Landed) Relocate(
        EffectRun run,
        ObjectId id,
        Zone to,
        Placement where,
        bool tapped,
        PlayerId? controller)
    {
        if (!run.State.Objects.TryGetValue(id, out var obj))
            return (run, null);
        var outcome = zones.Move(run.State, id, ZoneSystem.KeyFor(to, obj.Owner), where, tapped, controller);
        var landed = outcome.Events.OfType<ObjectMoved>().LastOrDefault(e => e.From == id)?.To.Id;
        return (Merge(run, outcome), landed);
    }

    /// <summary>
    /// What a search found, CR 701.23a: each card goes to the zone the effect names, and the
    /// library is shuffled afterwards, CR 701.23c.
    /// </summary>
    [RuleRef("701.23")]
    private EffectRun Found(EffectRun run, Cards.Search search, PlayerId searcher, ImmutableList<ObjectId> found)
    {
        var before = run.State;
        foreach (var id in found)
            (run, _) = Relocate(run, id, search.To, search.Where, search.Tapped, controller: null);
        if (search.ShuffleAfter && search.From == Zone.Library)
            run = Merge(run, zones.Shuffle(run.State, searcher));
        return Recall(run, search.RememberAs, Landed(before, run));
    }

    /// <summary>
    /// The cards a player looked at and then put somewhere, in the order they answered: the
    /// bottom of the library after a scry (CR 701.22a) or the graveyard after a surveil
    /// (CR 701.25a). The ones not named stay on top in the order they were.
    /// </summary>
    [RuleRef("701.22")]
    private EffectRun Sorted(EffectRun run, IEnumerable<ObjectId> chosen, Zone to, Placement where)
    {
        foreach (var id in chosen)
            (run, _) = Relocate(run, id, to, where, tapped: false, controller: null);
        return run;
    }

    private EffectRun Look(EffectRun run, PlayerRef? who, Amount count, string kind, string prompt)
    {
        var player = FirstPlayer(run, who);
        var top = run.State.In(ZoneKey.Of(Zone.Library, player)).Take(Math.Max(0, Number(run, count))).ToList();
        return top.Count == 0 ? run : Ask(run, player, kind, prompt, OptionsFor(run.State, top), 0, top.Count);
    }

    private ImmutableList<ObjectId> Controlled(GameState state, PlayerId player, ObjectFilter? filter, EffectContext context) =>
    [
        .. state.In(ZoneKey.Shared(Zone.Battlefield))
            .Where(id => characteristics.ControllerOf(state, id) == player)
            .Where(id => filter is null || query.Matches(state, filter, id, context)),
    ];

    /// <summary>The objects that exist now and did not before: what a batch of moves or creations produced.</summary>
    private static IEnumerable<ObjectId> Landed(GameState before, EffectRun run) =>
        run.State.Objects.Keys.Where(id => !before.Objects.ContainsKey(id));
}
