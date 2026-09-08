using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// The structural arms, CR 608.2: how instructions are combined. Everything here either runs a
/// sub-effect, binds something into the context for the sub-effect to read, or stops the walk
/// with a question.
/// </summary>
public sealed partial class EffectInterpreter
{
    private EffectRun? Structure(EffectRun run, Effect effect)
    {
        switch (effect)
        {
            case Sequence sequence:
                {
                    var index = 0;
                    foreach (var step in sequence.Steps)
                    {
                        run = Child(run, index++, step);
                        if (run.Halted)
                            break;
                    }

                    return run;
                }

            // CR 608.2a: an "if" inside an effect is checked as the effect resolves.
            case If test:
                return query.Holds(run.State, test.Test, run.Context)
                    ? Child(run, 0, test.Then)
                    : test.Else is { } otherwise ? Child(run, 1, otherwise) : run;

            // CR 608.2: "you may" is a question, and the game stops until it is answered.
            case May may:
                {
                    var who = FirstPlayer(run, may.Who);
                    if (may.StoreChoiceAs is { } key && run.Context.Chosen.TryGetValue(key, out var answered))
                        return Yes(answered) ? Inside(run, key, may.Do) : run;
                    if (may.StoreChoiceAs is null && run.Context.Chosen.TryGetValue("may:" + run.Path, out var inline))
                        return Yes(inline) ? Inside(run, "may:" + run.Path, may.Do) : run;
                    return Ask(run, who, "may", "You may do this.", YesNo, 1, 1, may.StoreChoiceAs ?? "may:" + run.Path);
                }

            // CR 118.8: the effect happens unless the named player pays; paying is a question too.
            // CR 118.6 with 601.2h (E3p ask 4): the cost is paid in full through the casting system or
            // not at all — a player who cannot pay is not asked, and a "yes" that no longer goes
            // through is a "no".
            case Unless unless:
                {
                    var who = FirstPlayer(run, unless.Who);
                    var key = "unless:" + run.Path;
                    if (run.Context.Chosen.TryGetValue(key, out var paid))
                    {
                        if (!Yes(paid) || Casting is null)
                            return Yes(paid) ? run : Child(run, 0, unless.Do);
                        return Casting.CanPayForRule(run.State, who, unless.Paid, run.Context.Source) is null
                            ? Merge(run, Casting.PayForRule(run.State, who, unless.Paid, run.Context.Source))
                            : Child(run, 0, unless.Do);
                    }

                    if (Casting is not null && Casting.CanPayForRule(run.State, who, unless.Paid, run.Context.Source) is not null)
                        return Child(run, 0, unless.Do);
                    return Ask(run, who, "unless", "Pay the cost to stop this?", YesNo, 1, 1, key);
                }

            // CR 608.2: "for each" runs once per object, with that object as the triggering one.
            // The objects are worked out once and remembered (CR 101.4): a question raised in the
            // third iteration is answered against a state the first two changed, and re-resolving
            // the selector then would put a different object at the index the answer names.
            case ForEach each:
                {
                    var outer = run.Context;
                    ImmutableList<ObjectId> over = Marked(run) is { } resumed
                        ? [.. resumed.Select(id => new ObjectId(id))]
                        : Objects(run, each.Over);
                    run = Mark(run, over.Select(id => id.Value));
                    var index = 0;
                    foreach (var id in over)
                    {
                        run = Forgetting(run with { Context = run.Context with { Triggering = [id] } }, index, each.Do);
                        run = Child(run, index++, each.Do);
                        if (run.Halted)
                            break;
                    }

                    return Unmark(run) with { Context = run.Context with { Triggering = outer.Triggering } };
                }

            case ForEachPlayer each:
                {
                    var outer = run.Context;
                    ImmutableList<PlayerId> over = Marked(run) is { } resumed
                        ? [.. resumed.Select(seat => new PlayerId(seat))]
                        : PlayersOrYou(run, each.Over);
                    run = Mark(run, over.Select(player => player.Value));
                    var index = 0;
                    foreach (var player in over)
                    {
                        run = run with { Context = run.Context with { TriggeringPlayer = player } };
                        run = Child(run, index++, each.Do);
                        if (run.Halted)
                            break;
                    }

                    return Unmark(run) with { Context = run.Context with { TriggeringPlayer = outer.TriggeringPlayer } };
                }

            case Repeat repeat:
                {
                    var times = Math.Max(0, Number(run, repeat.Times));
                    for (var i = 0; i < times && !run.Halted; i++)
                        run = Child(run, i, repeat.Do);
                    return run;
                }

            // CR 700.2: the modes were chosen as the spell was cast; resolution just runs them.
            case ChooseMode modal:
                {
                    if (!run.Context.Chosen.TryGetValue("modes", out var chosen))
                    {
                        // CR 700.2 with 601.2b: the ceiling was worked out as the spell or ability
                        // was put on the stack, and a conditional one (MaxIf) rode there under
                        // ModeAnnouncement.CeilingKey. Reading it back is how a question asked at
                        // resolution offers the number the announcement came to; a spell whose
                        // ceiling was never conditional carries no key and keeps modal.Max.
                        var ceiling = Ceiling(run, modal);
                        return Ask(
                            run,
                            run.Context.Controller,
                            "mode",
                            "Choose.",
                            [.. modal.Modes.Select((m, i) => new ChoiceOption(i.ToString(CultureInfo.InvariantCulture), m.Text))],
                            Math.Min(modal.Min, ceiling),
                            ceiling,
                            "modes");
                    }

                    foreach (var index in Indices(chosen))
                    {
                        if (index < 0 || index >= modal.Modes.Count)
                            continue;
                        run = Child(run, index, modal.Modes[index].Do);
                        if (run.Halted)
                            break;
                    }

                    return run;
                }

            case ChooseValue choose:
                {
                    if (run.Context.Chosen.TryGetValue(choose.Key, out var value))
                        return Kept(run, choose.Key, value);

                    var offered = ValueOptions(run.State, choose);

                    // CR 601.2b with 101.4: a question nobody can answer stops the game for ever,
                    // so a kind of value with no set to draw from is refused here rather than asked.
                    if (offered.Count == 0)
                        throw new RefusedException(
                            $"a choice of {choose.What} has nothing to choose from: the effect names no options and the game defines no set for it",
                            "601.2b");

                    return Ask(
                        run,
                        FirstPlayer(run, choose.Who),
                        ChoiceKinds.Value,
                        "Choose " + choose.What.ToString(),
                        offered,
                        1,
                        1,
                        choose.Key);
                }

            case Remember remember:
                return Recall(run, remember.Key, Objects(run, remember.What));

            case Store store:
                return run with
                {
                    Context = run.Context with { Stored = run.Context.Stored.SetItem(store.Key, Number(run, store.Value, 0)) },
                };

            // CR 603.7: a delayed triggered ability is created now and triggers later, once.
            case DelayedTrigger delayed:
                return Delayed(run, delayed);

            case DoNothing:
                return run;

            // The translator could not say this in the vocabulary; it must never reach a table.
            // CR 608.2: the resolution cannot be performed, so the definition is refused by name
            // and with the reason the translator wrote, which a runner reports as a failure. A
            // thrown NotSupportedException was a crash at the engine's front door instead.
            case Unsupported unsupported:
                throw new RefusedException(
                    $"the definition carries an {nameof(Unsupported)} effect and cannot resolve: {unsupported.Reason}",
                    "608.2");

            default:
                return null;
        }
    }

    /// <summary>
    /// What a "choose a colour" offers, CR 601.2b: the choice is made from a set the game defines,
    /// so the question carries that set and any answer in it is a legal answer. A card that narrows
    /// the set writes it on the effect (<see cref="ChooseValue.Options"/>) and that wins; otherwise
    /// the set is rules data — the five colours of CR 105.1, the creature types of CR 205.3m, the
    /// basic land types of CR 205.3i, the card types of CR 205.2a — read off the same tables the
    /// rest of the engine reads, never a list written here. The option's id is the word the answer
    /// is stored as, which is the word every reader of a chosen value parses: a colour goes back
    /// through <c>Enum.TryParse&lt;Color&gt;</c>, a type through the subtype tables.
    /// <para>
    /// A kind with no closed set — a card name, a word, a number the card gave no range for — has
    /// no options unless the effect wrote them, and the caller refuses rather than asking a
    /// question with an empty list, which CR 101.4 would hold the game on for ever.
    /// </para>
    /// </summary>
    [RuleRef("601.2b")]
    private static ImmutableList<ChoiceOption> ValueOptions(GameState state, ChooseValue choose)
    {
        if (choose.Options is { Count: > 0 } narrowed)
            return Offer(narrowed);

        return choose.What switch
        {
            // CR 105.1: the five colours, in the order the rules name them.
            ChooseWhat.Color => Offer(Enum.GetNames<Color>().Where(c => c != nameof(Color.Colorless)), sorted: false),
            ChooseWhat.CreatureType => Offer(CreatureTypes.All),
            ChooseWhat.BasicLandType => Offer(LandTypes.Basic),
            ChooseWhat.CardType => Offer(Enum.GetNames<CardType>().Where(t => t != nameof(CardType.None))),
            ChooseWhat.Keyword => Offer(Enum.GetNames<KeywordKind>()),

            // CR 201.3: a card's name is what is printed on it, so a name choice with no printed
            // options is made from the names the game holds, narrowed by OfType (CR 205.2a).
            ChooseWhat.CardName => Names(state, choose.OfType),

            // CR 700.3: "odd or even" is a choice between the two words themselves.
            ChooseWhat.OddOrEven => Offer(["Odd", "Even"], sorted: false),

            // CR 601.2b: a player is chosen from the players in the game; the seat is the id so the
            // answer is the same number every other player-shaped answer carries.
            ChooseWhat.Player => [.. state.Players.Select(p => new ChoiceOption(p.Id.Value.ToString(CultureInfo.InvariantCulture), p.Name))],
            _ => [],
        };
    }

    private static ImmutableList<ChoiceOption> Offer(IEnumerable<string> words, bool sorted = true) =>
    [
        .. (sorted ? words.Order(StringComparer.Ordinal) : words).Select(w => new ChoiceOption(w, w)),
    ];

    /// <summary>
    /// Keep the value the card chose, CR 608.2d. The choice a resolution makes lives in that
    /// resolution's context and dies with it, but "the chosen color" is asked again later — by a
    /// triggered ability of the same permanent, by a devotion count, by a mana ability — so the
    /// object that chose it keeps it. Only a permanent does: an object that changes zones is a new
    /// object and CR 400.7 leaves the choice behind with the old one, and a spell's choices go with
    /// the spell. A value already stored is not stored again, so reading a stored choice back on a
    /// later resolution emits nothing and asks nobody.
    /// </summary>
    [RuleRef("608.2d")]
    private static EffectRun Kept(EffectRun run, string key, string value)
    {
        if (run.Context.Source is not { } source
            || !run.State.Objects.TryGetValue(source, out var obj)
            || obj.Zone.Zone != Cards.Zone.Battlefield
            || (obj.Chosen.TryGetValue(key, out var stored) && string.Equals(stored, value, StringComparison.Ordinal)))
            return run;

        return Emit(run, new Events.ValueChosen(source, key, value));
    }

    /// <summary>
    /// Create the delayed triggered ability, CR 603.7 — one of them, except for the case CR 106.6a
    /// names. An ability that produced keyed mana and then says "when that mana is spent" creates
    /// <em>a separate delayed ability for each mana produced</em>, so a replacement that made two
    /// mana where the card printed one gets two abilities and each fires for its own mana. Which
    /// mana each one watches is the key the <see cref="AddMana"/> wrote on that mana's provenance,
    /// so the two halves are linked through the pool and not through this node. An ability whose
    /// key produced no mana creates nothing to watch it with; a key the same resolution did not
    /// produce is left as the card wrote it, because CR 106.6 lets a linked ability elsewhere have
    /// made the mana.
    /// </summary>
    [RuleRef("603.7")]
    private EffectRun Delayed(EffectRun run, DelayedTrigger delayed)
    {
        if (delayed.When is ManaSpent spent && Keys(run.State, run.Context.Source, spent.ManaKey) is { Count: > 0 } keys)
        {
            foreach (var key in keys)
                run = Delaying(run, delayed, spent with { ManaKey = key });
            return run;
        }

        return Delaying(run, delayed, delayed.When);
    }

    [RuleRef("603.7")]
    private static EffectRun Delaying(EffectRun run, DelayedTrigger delayed, Trigger when) =>
        Emit(run, new DelayedTriggerCreated(new DelayedTriggerState(
            run.State.NextTimestamp,
            when,
            delayed.Do,
            run.Context.Source,
            run.Context.Controller,
            delayed.Once,
            run.Context.Remembered,
            delayed.Until)));

    /// <summary>
    /// The mana this source produced under that key, CR 106.6a, one entry per single mana. Empty
    /// when nothing in any pool carries it: the mana has been spent already, or another permanent's
    /// linked ability is the one that will make it (CR 106.6).
    /// </summary>
    [RuleRef("106.6a")]
    private static ImmutableList<string> Keys(GameState state, ObjectId? source, string key)
    {
        if (source is null)
            return [];

        var prefix = key + "#";
        var keys = ImmutableList.CreateBuilder<string>();
        foreach (var player in state.Players)
        {
            foreach (var block in player.Mana.Provenance)
            {
                if (block.Source != source || block.Key is not { } written || !written.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                for (var i = 0; i < block.Amount; i++)
                    keys.Add(written);
            }
        }

        return keys.ToImmutable();
    }

    /// <summary>
    /// Run what a "you may" says yes to, telling the walk which answer it is inside. A
    /// <see cref="PayCost"/> in there that cannot be paid puts that answer back to no, so the
    /// "if you do" half reads false rather than running on an unpaid cost (CR 118.12).
    /// </summary>
    [RuleRef("118.12")]
    private EffectRun Inside(EffectRun run, string key, Effect body)
    {
        var inner = Child(run with { PayKey = key }, 0, body);
        return inner with { PayKey = run.PayKey };
    }

    private static bool Yes(string answer) => answer is "yes" or "true" or "1";

    private static IEnumerable<int> Indices(string chosen) => chosen
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => int.TryParse(s, CultureInfo.InvariantCulture, out var n) ? n : -1);

    /// <summary>
    /// The path of the sub-effect about to run. A pending question records it, so resuming is a
    /// branch on the kind and the path, never a captured continuation.
    /// </summary>
    private static EffectRun Descend(EffectRun run, int index) =>
        run with { Path = run.Path + "." + index.ToString(CultureInfo.InvariantCulture) };

    /// <summary>
    /// How many modes the resolution's question may take, CR 700.2 with 601.2b. The casting system
    /// reads a conditional ceiling (<c>MaxIf</c>/<c>MaxIfMet</c>) once, as the spell or ability is
    /// announced, and writes the number it came to onto the stack entry under
    /// <see cref="ModeAnnouncement.CeilingKey"/>; the condition is never asked again, so the
    /// interpreter reads the recorded number rather than the record's own <c>MaxIf</c>. Nothing is
    /// recorded for an ordinary modal spell, and one whose key does not parse is that spell too.
    /// </summary>
    [RuleRef("700.2")]
    private static int Ceiling(EffectRun run, ChooseMode modal) =>
        run.Context.Chosen.TryGetValue(ModeAnnouncement.CeilingKey, out var announced)
        && int.TryParse(announced, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ceiling)
            ? ceiling
            : modal.Max;

    /// <summary>
    /// CR 700.2 with 608.2: a choice inside "for each" is made once per iteration, so a keyed
    /// <see cref="Choose"/> the body reads is forgotten — its memory and its answer — as each
    /// iteration begins, and the body asks again. Two iterations keep what they have: one the walk
    /// is skipping on its way back to a question (CR 101.4), and the one whose question is being
    /// answered, which must read that answer rather than ask it a second time.
    /// </summary>
    [RuleRef("700.2")]
    private static EffectRun Forgetting(EffectRun run, int index, Effect body)
    {
        var iteration = Descend(run, index).Path;
        if (run.Resume is { } target && !OnPath(iteration, target))
            return run;
        if (run.AnswerPath is { } answered && OnPath(iteration, answered))
            return run;

        var context = run.Context;
        foreach (var choose in Cards.Linter.Walk(body).OfType<Choose>())
        {
            if (choose.Key is not { } key)
                continue;
            context = context with { Remembered = context.Remembered.Remove(key), Chosen = context.Chosen.Remove(key) };
        }

        return run with { Context = context };
    }
}
