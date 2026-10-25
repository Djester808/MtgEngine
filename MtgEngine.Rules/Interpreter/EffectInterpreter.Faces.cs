using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Faces;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// The verbs that change which face of a card is up, and the ones that copy an object. Every
/// decision about whether a card can turn over at all belongs to <c>Faces/FaceRules.cs</c> and
/// every decision about what a copy takes belongs to <c>Faces/Copies.cs</c>; this file only turns
/// what those pure functions answer into events, so there is one reading of CR 701.27, 710, 708
/// and 707 in the engine rather than two.
/// </summary>
public sealed partial class EffectInterpreter
{
    private EffectRun? FaceVerb(EffectRun run, Effect effect)
    {
        switch (effect)
        {
            // CR 701.27a: transform a permanent. CR 701.28a makes convert the same game action
            // under a different word, and CR 710.2 is what a flip card does instead; which of
            // them a card can do is its layout, which is data, so one arm covers all three.
            case Cards.Transform transform:
                return Turn(run, Objects(run, transform.What), TurnKind.Transform);

            case Cards.Convert convert:
                return Turn(run, Objects(run, convert.What), TurnKind.Convert);

            // CR 708.4 and 708.2: a permanent is turned face up or face down; it keeps its face.
            case TurnFaceUp up:
                return Show(run, Objects(run, up.What), faceUp: true);

            case TurnFaceDown down:
                return Show(run, Objects(run, down.What), faceUp: false);

            // CR 701.42a: two cards of a meld pair become one permanent represented by both.
            case Cards.Meld meld:
                return Melding(run, Objects(run, meld.With));

            // CR 707.10: a copy of a spell is put on the stack; it is not cast.
            case CopySpell copy:
                return CopyOnStack(run, copy);

            default:
                return null;
        }
    }

    /// <summary>
    /// Turn a permanent over, CR 701.27a, 701.28a and 710.2. <c>FaceRules</c> answers whether it
    /// turns at all — a single-faced card, a meld card, an instant or sorcery back face and an
    /// already-flipped permanent all turn nothing over (CR 701.27c/d, 712.4c, 712.10, 710.4) —
    /// and when it answers no, no event is emitted, so nothing triggers.
    /// </summary>
    [RuleRef("701.27")]
    private EffectRun Turn(EffectRun run, IEnumerable<ObjectId> ids, TurnKind kind)
    {
        foreach (var id in ids)
        {
            if (!run.State.Objects.TryGetValue(id, out var obj))
                continue;

            var flips = obj.Definition.Layout == Layout.Flip;
            var turned = flips ? FaceRules.Flip(obj.Definition, obj.FaceIndex)
                : kind == TurnKind.Convert ? FaceRules.Convert(obj.Definition, obj.FaceIndex)
                : FaceRules.Transform(obj.Definition, obj.FaceIndex);
            if (!turned.Turned)
                continue;

            run = Emit(run, new ObjectTurnedOver(id, turned.FaceIndex, flips ? TurnKind.Flip : kind));
        }

        return run;
    }

    /// <summary>
    /// CR 708: turning a permanent face up or face down. A permanent already the way it is being
    /// turned does not turn, so nothing happens and nothing triggers (CR 708.4).
    /// </summary>
    [RuleRef("708")]
    private EffectRun Show(EffectRun run, IEnumerable<ObjectId> ids, bool faceUp)
    {
        foreach (var id in ids)
        {
            if (!run.State.Objects.TryGetValue(id, out var obj) || obj.FaceDown != faceUp)
                continue;
            run = Emit(run, new ObjectTurnedOver(id, obj.FaceIndex, faceUp ? TurnKind.TurnedFaceUp : TurnKind.TurnedFaceDown));
        }

        return run;
    }

    /// <summary>
    /// CR 701.42: melding. <c>FaceRules.Meld</c> says whether these two cards are a meld pair and
    /// which face the combined permanent reads; CR 701.42c leaves two cards that are not a pair
    /// exactly where they are, which is the whole of what can be done today — a melded permanent
    /// is one object represented by two cards (CR 701.42a) and <c>GameObject</c> holds one.
    /// </summary>
    [RuleRef("701.42")]
    private EffectRun Melding(EffectRun run, IEnumerable<ObjectId> with)
    {
        if (run.Context.Source is not { } sourceId || !run.State.Objects.TryGetValue(sourceId, out var first))
            return run;

        foreach (var id in with)
        {
            if (!run.State.Objects.TryGetValue(id, out var second))
                continue;

            // CR 701.42c: cards that are not a meld pair do not meld and do not move.
            if (!FaceRules.Meld(first.Definition, second.Definition).Success)
                continue;

            throw new RefusedException(
                "A melded permanent needs GameObject.Components: CR 701.42a makes it one object represented by two cards, "
                + "and CR 712.4b unmelds them when it leaves; the seam is asked for in seam-requests/E9b.md",
                "701.42a");
        }

        return run;
    }

    /// <summary>
    /// CR 707.10: a copy of a spell on the stack. The copy has the copiable values and nothing
    /// else (CR 707.2); it keeps the original's targets unless the copy's controller is allowed
    /// to change them, and it is a copy, so it ceases to exist rather than going to a graveyard.
    /// </summary>
    [RuleRef("707.10")]
    private EffectRun CopyOnStack(EffectRun run, CopySpell copy)
    {
        var created = new List<ObjectId>();
        // CR 707.10 with 702.40a: no amount means one copy; an amount of 0 — a storm count with
        // no earlier spell — makes none, so 0 is read as 0 and only an absent N as 1.
        var times = copy.N is null ? 1 : Math.Max(0, Number(run, copy.N));
        foreach (var id in Objects(run, copy.What))
        {
            if (!run.State.Objects.TryGetValue(id, out var spell) || spell.Stack is not { } entry)
                continue;
            for (var i = 0; i < times; i++)
            {
                var made = New(run, CopyRules.CopyOf(spell), run.Context.Controller, ZoneKey.Shared(Zone.Stack), isToken: false);
                run = Emit(run, new ObjectCreated(made with { Stack = entry with { IsCopy = true } }, -1));
                created.Add(made.Id);
            }
        }

        // CR 707.10: "the controller of the copy may choose new targets for it." The flag has
        // been on the record since wave 0 and KeywordExpander sets it explicitly; nothing read
        // it, so a Fork could never be pointed anywhere else and a storm copy was locked to the
        // original's target.
        if (copy.MayChooseNewTargets)
        {
            run = ChooseCopyTargets(run, [.. created]);
            if (run.Halted)
                return run;
        }

        return Recall(run, copy.RememberAs, created);
    }

    /// <summary>
    /// CR 707.10: each copy's controller may choose new targets for it. One question a copy,
    /// asked in the order the copies were made; the copy being asked about rides on the question
    /// (<see cref="CopyKey"/>) so a resumed walk retargets the one that was asked and moves on to
    /// the next rather than starting the row again.
    /// </summary>
    [RuleRef("707.10")]
    private EffectRun ChooseCopyTargets(EffectRun run, ImmutableList<ObjectId> copies)
    {
        var pending = copies;

        // An answer in hand belongs to the copy the question named; retarget it and drop it from
        // the row, so the walk asks about the next one and not this one again.
        if (Answered(run) is { } answered
            && run.Answer?.Context.Extra.TryGetValue(CopyKey, out var asked) == true
            && int.TryParse(asked, NumberStyles.Integer, CultureInfo.InvariantCulture, out var askedId))
        {
            var about = new ObjectId(askedId);
            if (run.State.Objects.TryGetValue(about, out var made) && made.Stack is { } madeEntry)
            {
                run = Retarget(run, about, madeEntry, Named(run.State, answered), NamedPlayers(run.State, answered));
            }

            pending = [.. copies.SkipWhile(id => id != about).Skip(1)];
        }

        foreach (var id in pending)
        {
            if (!run.State.Objects.TryGetValue(id, out var made) || made.Stack is not { } entry)
                continue;

            var effect = EffectOf(made, entry);
            var filters = effect is null ? [] : EffectWalk.TargetsIn(effect);
            var controller = characteristics.ControllerOf(run.State, id);
            var context = ContextFor(run.State, made, entry, controller, effect);
            var candidates = Candidates(run.State, filters, context);
            var players = PlayerCandidates(run.State, filters, controller);
            var count = entry.Targets.Count + Targeted(entry.TargetPlayers, entry).Count;

            // A copy that targets nothing has nothing to re-aim, and CR 707.10 asks nobody.
            if (count == 0 || candidates.Count + players.Count == 0)
                continue;

            return Ask(
                run,
                controller,
                "target",
                "Choose new targets for the copy.",
                OptionsFor(run.State, candidates).AddRange(PlayerOptions(run.State, players)),
                count,
                count,
                shape: context => context with
                {
                    Extra = context.Extra.SetItem(CopyKey, id.Value.ToString(CultureInfo.InvariantCulture)),
                });
        }

        return run;
    }

    /// <summary>Which copy a "choose new targets" question is about, CR 707.10.</summary>
    private const string CopyKey = "copy";

    /// <summary>
    /// CR 707.2 and 111.4: a token that is a copy of a permanent. It takes the copiable values —
    /// the definition and the face that is up — and none of the counters, damage or status the
    /// thing it copies happens to have, because those were never copiable in the first place. It
    /// arrives through the zone system's copiable-values <c>CreateToken</c>, the same door as every
    /// other token, so CR 614.1c reaches it and the interpreter is not a second place that knows
    /// how an object enters the battlefield.
    /// </summary>
    [RuleRef("707.2")]
    private EffectRun CopyToken(EffectRun run, Cards.CreateToken token, Selector of)
    {
        var created = new List<ObjectId>();
        var count = Math.Max(0, Number(run, token.N));
        var sources = Objects(run, of);
        foreach (var player in PlayersOrYou(run, token.Who))
        {
            foreach (var id in sources)
            {
                if (!run.State.Objects.TryGetValue(id, out var source))
                    continue;
                for (var i = 0; i < count; i++)
                {
                    var before = run.State;
                    run = Merge(run, zones.CreateToken(run.State, CopyRules.CopyOf(source), player, token.Tapped, token.Attacking));
                    created.AddRange(run.State.Objects.Keys.Where(made => !before.Objects.ContainsKey(made)));
                }
            }
        }

        return Recall(run, token.RememberAs, created);
    }

    /// <summary>The new object a copy is, with the next free id and timestamp, CR 707.2.</summary>
    private static GameObject New(EffectRun run, CopiableValues values, PlayerId controller, ZoneKey zone, bool isToken) =>
        CopyRules.NewCopy(
            values,
            new ObjectId(run.State.NextObjectId),
            controller,
            controller,
            zone,
            run.State.NextTimestamp,
            isToken);
}
