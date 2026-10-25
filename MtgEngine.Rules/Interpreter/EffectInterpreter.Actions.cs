using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
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
                return MoveObjects(run, move);

            // CR 701.8b: a permanent with indestructible is not destroyed.
            case Destroy destroy:
                {
                    // CR 701.8: "for each creature destroyed this way" counts what the destruction
                    // actually destroyed, so the key is set from the objects that changed zones and
                    // not from the ones the selector named — an indestructible permanent, one whose
                    // regeneration shield was used (CR 701.19a) and one already gone are all absent.
                    var before = run.State;
                    run = Leave(run, Objects(run, destroy.What), "destroyed", Zone.Graveyard, skipIndestructible: true);
                    return Recall(run, destroy.RememberAs, Landed(before, run));
                }

            case Cards.Exile exile:
                {
                    // CR 701.13 with the plan's fourth rule: an exile this engine cannot perform
                    // is refused rather than performed wrongly. Until has no reader anywhere, so
                    // "exile until this leaves the battlefield" exiled the card for EVER - a card
                    // silently destroyed - and no card in the corpus writes it, so refusing it costs
                    // nothing that works today.
                    if (exile.Until is not null)
                        throw new RefusedException("An exile with a duration needs the return that ends it; see seam-requests.", "701.13");

                    // ExiledWith is NOT refused, though it has no reader either. A refusal here is
                    // not a quiet gap: Game.Apply turns a RefusedException into a Refused answer and
                    // throws the whole command away, so The Darkness Crystal's "instead exile it and
                    // you gain 2 life" would neither exile nor gain - the creature would simply die,
                    // and the player's command would be rejected. The exile itself is exactly right;
                    // only the link back is missing, and that is the third ability's problem, which
                    // needs a selector that can read it AND a remembered key that outlives one
                    // resolution (seam-requests/EINT7.md). Refusing would break the two thirds that
                    // work to report the third that does not.

                    var ids = Objects(run, exile.What);
                    var before = run.State;
                    run = Leave(run, ids, "exiled", Zone.Exile, skipIndestructible: false, faceDown: exile.FaceDown);
                    var landed = Landed(before, run);

                    return Recall(run, exile.RememberAs, landed);
                }

            // CR 701.21a: sacrificing is the controller's own action and cannot be replaced.
            case Sacrifice sacrifice:
                {
                    if (sacrifice.What is { } named)
                        return Leave(run, Objects(run, named), "sacrificed", Zone.Graveyard, skipIndestructible: false);

                    // CR 701.21 with 110.4: "a permanent of each permanent type, then sacrifice the
                    // rest" is a different question from "sacrifice N" — the arm is in Wave29.cs.
                    if (sacrifice.KeepOnePerPermanentType)
                        return KeepingOnePerType(run, sacrifice);

                    // CR 701.16 with 102.1: "each player sacrifices" reaches every player the
                    // reference admits, in APNAP order (CR 101.4), and each of them chooses their
                    // own — so the walk asks one player, stops, and the answer puts it back here to
                    // move that player's permanent and ask the next of them.
                    var sacrificing = InTurnOrder(run.State, PlayersOrYou(run, sacrifice.Who));
                    var next = 0;
                    if (Answered(run) is { } given && run.Answer is { } answer)
                    {
                        run = Leave(run, Named(run.State, given), "sacrificed", Zone.Graveyard, skipIndestructible: false);
                        next = sacrificing.IndexOf(answer.Player) + 1;
                    }

                    foreach (var chooser in sacrificing.Skip(next))
                    {
                        var options = OptionsFor(run.State, Controlled(run.State, chooser, sacrifice.Choice, run.Context));

                        // CR 701.16b: a player who controls nothing the effect admits sacrifices
                        // nothing, and is not asked a question no answer could satisfy (CR 101.4).
                        if (options.Count == 0)
                            continue;
                        var count = Math.Min(options.Count, Math.Max(1, Number(run, sacrifice.N)));
                        return Ask(run, chooser, ChoiceKinds.Sacrifice, "Choose what to sacrifice.", options, count, count);
                    }

                    return run;
                }

            // CR 701.6a: a countered spell goes to its owner's graveyard unless told otherwise —
            // "put it on top of their library instead" is one action, so the spell is put where the
            // record says and never reaches a graveyard on the way, and the placement travels with
            // the zone rather than being applied by a second move afterwards.
            case Cards.Counter counter:
                return Leave(run, Objects(run, counter.What), "countered", counter.PutInto ?? Zone.Graveyard, skipIndestructible: false, where: counter.Where);

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
                            // CR 111.3: an amount for the size is evaluated as the token is created
                            // and written into the spec, so it is the token's own copiable text.
                            run = Merge(run, zones.CreateToken(run.State, Sized(run, token.Token), player, token.Tapped, token.Attacking));
                            var arrived = run.State.Objects.Keys.Where(id => !before.Objects.ContainsKey(id)).ToList();

                            // CR 508.4: the token attacks the player the effect names, not whoever
                            // the attackers already declared were declared against.
                            if (token.AttackingWhom is { } whom)
                                run = Attacking(run, arrived, whom);
                            created.AddRange(arrived);
                        }
                    }

                    return Recall(run, token.RememberAs, created);
                }

            // CR 701.9a with 701.9b: the affected player chooses which card by default; what may be
            // discarded and who picks it are the record's own fields. The arm is in Wave29.cs.
            case Cards.Discard discard:
                return Discarding(run, discard);

            // CR 701.40a: manifest — the card is turned face down and put onto the battlefield as
            // a 2/2 face-down creature. The arm is in Wave28.cs with the rule it reads.
            case Cards.Manifest manifest:
                return Manifesting(run, manifest);

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
                return Searching(run, search);

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
    private EffectRun Leave(EffectRun run, IEnumerable<ObjectId> ids, string verb, Zone to, bool skipIndestructible, Placement where = Placement.Default, bool faceDown = false)
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

            (run, _) = Relocate(run, id, to, where, tapped: false, controller: null, faceDown: faceDown);
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
        var ownerKey = ":shuffle-owners:" + run.Path;
        var owners = run.Answer?.Context.Extra.TryGetValue(ownerKey, out var saved) == true
            ? Numbers(saved).Select(value => new PlayerId(value)).ToList()
            : new List<PlayerId>();
        if (shuffle.IntoLibrary is { } what)
        {
            foreach (var id in MovementObjects(ref run, what))
            {
                if (run.State.Objects.TryGetValue(id, out var obj) && !owners.Contains(obj.Owner))
                    owners.Add(obj.Owner);
                run = run with
                {
                    Marks = (run.Marks ?? ImmutableDictionary<string, string>.Empty)
                        .SetItem(ownerKey, Joined(owners.Select(owner => owner.Value))),
                };
                (run, _) = Relocate(run, id, Zone.Library, Placement.Default, tapped: false, controller: null);
                if (run.Halted)
                    return run;
            }
        }

        // Each owner whose card went in, plus every player the effect named itself; an effect that
        // named no object shuffles "you", which is what PlayersOrYou answers for a null Who.
        var players = shuffle.IntoLibrary is null || shuffle.Who is not null
            ? owners.Concat(PlayersOrYou(run, shuffle.Who))
            : owners;
        foreach (var player in players.Distinct())
        {
            run = ProposeThrough(run, current => zones.Shuffle(current.State, player, current.Context.UsedReplacements));
            if (run.Halted)
                return run;
        }
        run = ForgetMovement(run);
        return run with { Marks = run.Marks?.Remove(ownerKey) };
    }

    /// <summary>One zone change through the zone system, and the id the object came back with, CR 400.7.</summary>
    private (EffectRun Run, ObjectId? Landed) Relocate(
        EffectRun run,
        ObjectId id,
        Zone to,
        Placement where,
        bool tapped,
        PlayerId? controller,
        bool faceDown = false,
        string? batchBoard = null)
    {
        if (!run.State.Objects.TryGetValue(id, out var obj))
            return (Ordered(run) is not null ? ProposeThrough(run, current => Outcome.Unchanged(current.State)) : run, null);
        if (to == Zone.Library && obj.Zone == ZoneKey.Of(Zone.Library, obj.Owner))
        {
            var eventCount = run.Events.Count;
            run = ProposeThrough(run, current =>
            {
                var cards = current.State.In(obj.Zone);
                var order = Library.LibraryOrder.Arrange(cards, where == Placement.Bottom ? [] : [id], where == Placement.Bottom ? [id] : []);
                if (cards.SequenceEqual(order))
                    return Outcome.Unchanged(current.State);
                var reordered = new ZoneReordered(obj.Zone, order);
                return new Outcome(reordered.Apply(current.State), [reordered]);
            });
            var change = run.Events.Skip(eventCount).OfType<ZoneReordered>().LastOrDefault();
            var positioned = change is null ? id : ReorderedIdentities(run, change).GetValueOrDefault(id, id);
            return (change is null ? run : RefreshDisclosedLibraryIdentities(run, change), positioned);
        }
        // CR 701.40a: a manifested card is turned face down before it is put onto the battlefield,
        // so the flag rides on the move itself; every other caller takes the plain road.
        var before = run.Events.Count;
        var moved = ProposeThrough(run, current =>
        {
            return batchBoard is not null
            ? zones.MoveInBatch(current.State, new MoveRequest(id, ZoneSystem.KeyFor(to, obj.Owner), where, tapped, controller), batchBoard,
                current.Context.UsedReplacements)
            : faceDown || current.Context.UsedReplacements?.Any() == true
            ? zones.Move(current.State, id, ZoneSystem.KeyFor(to, obj.Owner), where, tapped, controller, faceIndex: null, faceDown: faceDown,
                usedReplacements: current.Context.UsedReplacements)
            : zones.Move(current.State, id, ZoneSystem.KeyFor(to, obj.Owner), where, tapped, controller);
        });
        var landed = moved.Events.Skip(before).OfType<ObjectMoved>().LastOrDefault(e => e.From == id)?.To.Id;
        return (moved, landed);
    }

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
