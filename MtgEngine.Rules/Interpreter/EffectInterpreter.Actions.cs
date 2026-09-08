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

            // CR 701.20a and 614.1: tapping and untapping are events, so "doesn't untap during
            // your untap step" and "can't become tapped" are replacements offered this one.
            case Cards.Tap tap:
                return Turned(run, Objects(run, tap.What), tapped: true);

            case Cards.Untap untap:
                return Turned(run, Objects(run, untap.What), tapped: false);

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

                    // CR 701.23f: the search is the event a Searches trigger watches, once per library
                    // and only when the search begins; the resumption above writes nothing (CR 701.23h),
                    // and a search of a graveyard or exile is not a library search.
                    foreach (var zone in Searched(search))
                    {
                        if (zone == Zone.Library)
                            run = Emit(run, new LibrarySearched(searcher, searcher));
                    }

                    // CR 701.23a: a search over several zones is one action over their union, so a
                    // single card is found across all of them. The zone being scanned is the
                    // search's to say, so a filter that named none is read in that zone.
                    var matching = Searched(search).SelectMany(zone =>
                    {
                        var filter = search.For.In is null ? search.For with { In = zone } : search.For;
                        return run.State.In(ZoneSystem.KeyFor(zone, searcher))
                            .Where(id => query.Matches(run.State, filter, id, run.Context));
                    });
                    var most = Math.Max(1, Number(run, search.UpTo));
                    return Ask(run, searcher, "search", "Search for a card.", OptionsFor(run.State, matching), search.UpTo is null ? 0 : 0, most);
                }

            case Cards.Shuffle shuffle:
                return Shuffling(run, shuffle);

            // CR 701.3: the two records the vocabulary spells attaching with are one meaning and
            // reach one arm; seam-requests/E9q.md asks the linter to keep AttachAction.
            case Cards.Attach attach:
                return Attaching(run, attach.What, attach.To);

            case AttachAction attach:
                return Attaching(run, attach.What, attach.To);

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

            // CR 614.1: the action itself is offered to the replacement system — "if it would be
            // countered, exile it instead", "regenerate" — and a replacement that drops the event
            // means the action does not happen, so the object does not change zones either. The
            // zone change that follows is offered by the zone system, not proposed twice here.
            var left = run.Events.Count;
            run = Propose(run, new ObjectLeftBy(id, verb, run.Context.Source), characteristics.ControllerOf(run.State, id));
            if (run.Halted)
                return run;
            if (!run.Events.Skip(left).OfType<ObjectLeftBy>().Any(e => e.Object == id))
                continue;

            (run, _) = Relocate(run, id, to, Placement.Default, tapped: false, controller: null);
        }

        return run;
    }

    /// <summary>
    /// Tapping or untapping one object at a time, each offered to the replacement system first
    /// (CR 614.1). A dropped event leaves the permanent as it was, which is what "can't become
    /// tapped" and "doesn't untap" mean.
    /// </summary>
    [RuleRef("614.1")]
    private EffectRun Turned(EffectRun run, IEnumerable<ObjectId> ids, bool tapped)
    {
        foreach (var id in ids)
        {
            if (!run.State.Objects.ContainsKey(id))
                continue;
            run = Propose(run, new ObjectChanged(id, Tapped: tapped), characteristics.ControllerOf(run.State, id));
            if (run.Halted)
                return run;
        }

        return run;
    }

    /// <summary>
    /// CR 701.24a: shuffling is one action on one library. When the effect names objects to shuffle
    /// in, they change zones first (CR 400.7) and the libraries they landed in are the ones
    /// shuffled — so "shuffle this spell into its owner's library" moves the resolving spell off
    /// the stack, and CR 608.2m, which puts a spell into its owner's graveyard only if nothing else
    /// moved it before the resolution ended, then finds nothing on the stack to move. A shuffle
    /// that names no object is the plain one and shuffles the library the effect says, as before.
    /// </summary>
    [RuleRef("608.2m")]
    private EffectRun Shuffling(EffectRun run, Cards.Shuffle shuffle)
    {
        var owners = new List<PlayerId>();
        if (shuffle.IntoLibrary is { } what)
        {
            foreach (var id in Objects(run, what))
            {
                if (!run.State.Objects.TryGetValue(id, out var obj))
                    continue;
                owners.Add(obj.Owner);
                (run, _) = Relocate(run, id, Zone.Library, Placement.Default, tapped: false, controller: null);
            }
        }

        // Each owner whose card went in, plus every player the effect named itself; an effect that
        // named no object shuffles "you", which is what PlayersOrYou answers for a null Who.
        var players = shuffle.IntoLibrary is null || shuffle.Who is not null
            ? owners.Concat(PlayersOrYou(run, shuffle.Who))
            : owners;
        foreach (var player in players.Distinct())
            run = Merge(run, zones.Shuffle(run.State, player));
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
    /// What a search found, CR 701.23a: each card goes to the zone the effect names — or, when the
    /// card names an "instead" destination it matches, there (<see cref="Destination"/>) — and then
    /// CR 701.23c shuffles the libraries that were searched — and only those, so a search of a
    /// graveyard shuffles nothing. The printed rider "if you do, shuffle" is
    /// <c>ShuffleOnlyIfFound</c>: no shuffle when nothing came out of the library.
    /// </summary>
    [RuleRef("701.23")]
    private EffectRun Found(EffectRun run, Cards.Search search, PlayerId searcher, ImmutableList<ObjectId> found)
    {
        var before = run.State;
        var library = ZoneSystem.KeyFor(Zone.Library, searcher);
        var fromLibrary = found.Any(id => before.Objects.TryGetValue(id, out var card) && card.Zone == library);
        foreach (var id in found)
        {
            var (to, where, tapped) = Destination(run, search, id);
            (run, _) = Relocate(run, id, to, where, tapped, controller: null);
        }

        if (search.ShuffleAfter && Searched(search).Contains(Zone.Library) && (!search.ShuffleOnlyIfFound || fromLibrary))
            run = Merge(run, zones.Shuffle(run.State, searcher));
        return Recall(run, search.RememberAs, Landed(before, run));
    }

    /// <summary>
    /// Where one card a search found goes, CR 701.23a: "put it into your hand. If it's a land card,
    /// put it onto the battlefield tapped instead". The card is read against each
    /// <c>Instead</c> entry's <c>Matching</c> in the order the card printed them, and the first
    /// entry that admits it wins, giving its <c>To</c> and <c>Tapped</c>; a card no entry admits
    /// goes to the search's own <c>To</c> with its <c>Where</c> and <c>Tapped</c>. The filter
    /// carries no zone (V10), so it is read against the card where it lies as it is found — the
    /// library, before it moves — whatever zone the record named: a filter defaulting to the
    /// battlefield would admit nothing. With several cards found each is read on its own, in the
    /// state the earlier ones left, so a card not yet moved is still where the search found it.
    /// </summary>
    [RuleRef("701.23a")]
    private (Zone To, Placement Where, bool Tapped) Destination(EffectRun run, Cards.Search search, ObjectId id)
    {
        if (search.Instead is { Count: > 0 } instead && run.State.Objects.TryGetValue(id, out var card))
        {
            foreach (var entry in instead)
            {
                var matching = entry.Matching with { In = card.Zone.Zone };
                if (query.Matches(run.State, matching, id, run.Context))
                    return (entry.To, Placement.Default, entry.Tapped);
            }
        }

        return (search.To, search.Where, search.Tapped);
    }

    /// <summary>
    /// The zones one search covers, CR 701.23a: the list the card printed, in printed order, or the
    /// single zone when it names one.
    /// </summary>
    [RuleRef("701.23")]
    private static ImmutableList<Zone> Searched(Cards.Search search) =>
        search.Zones is { Count: > 0 } zones ? [.. zones] : [search.From];

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
