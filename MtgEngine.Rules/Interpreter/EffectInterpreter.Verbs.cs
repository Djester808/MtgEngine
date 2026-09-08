using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// The verbs that change numbers: damage, life, counters, mana, continuous effects, turns and
/// the game itself. Damage goes through the replacement system because CR 616 says every event
/// is offered to the effects that would change it before it happens.
/// </summary>
public sealed partial class EffectInterpreter
{
    private EffectRun? Verb(EffectRun run, Effect effect)
    {
        switch (effect)
        {
            // CR 120.1: damage has a source and an amount, and the source's own characteristics
            // decide whether it is deathtouch damage, CR 702.2b.
            case DealDamage damage:
                {
                    var amount = Number(run, damage.N);
                    if (amount <= 0)
                        return run;
                    if (SourceOf(run, damage.Source) is not { } source)
                        return run;

                    var deathtouch = Has(run.State, source, KeywordKind.Deathtouch);
                    var commander = run.State.Objects.TryGetValue(source, out var from) && from.IsCommander;

                    // CR 115.4: "any target" was one choice made as the spell was put on the
                    // stack, and the entry holds it as an object or as a player. So the damage
                    // goes to whichever half is there, and never to both.
                    var any = damage.To is AnyTarget;
                    foreach (var id in any ? run.Context.Targets : Objects(run, damage.To))
                    {
                        if (!run.State.Objects.ContainsKey(id))
                            continue;

                        // CR 120.4a: where the effect sends excess damage, this one event is split
                        // — the permanent is dealt what would have been lethal, the rest goes on.
                        var excess = Excess(run, damage, id, amount, deathtouch);
                        if (amount > excess)
                        {
                            run = Propose(
                                run,
                                new DamageDealt(source, id, null, amount - excess, Combat: false, commander, deathtouch),
                                characteristics.ControllerOf(run.State, id));
                            if (run.Halted)
                                return run;
                        }

                        if (excess <= 0)
                            continue;

                        run = Redirected(run, damage, source, excess, commander, deathtouch);
                        if (run.Halted)
                            return run;
                    }

                    foreach (var player in any ? run.Context.TargetPlayers : Players(run, damage.ToPlayer))
                    {
                        run = Propose(run, new DamageDealt(source, null, player, amount, Combat: false, commander, deathtouch), player);
                        if (run.Halted)
                            return run;
                    }

                    return run;
                }

            // CR 121.1: drawing is the zone system's move, named as a draw for what watches it.
            case Cards.Draw draw:
                {
                    var count = Number(run, draw.N);
                    foreach (var player in PlayersOrYou(run, draw.Who))
                        run = Merge(run, zones.Draw(run.State, player, count));
                    return run;
                }

            // CR 119.3: life gain and loss are separate events, so effects can watch for one.
            case GainLife gain:
                return Life(run, gain.Who, Number(run, gain.N), "gainLife");

            case LoseLife lose:
                return Life(run, lose.Who, -Number(run, lose.N), "loseLife");

            case SetLife set:
                {
                    var target = Number(run, set.N);
                    foreach (var player in PlayersOrYou(run, set.Who))
                    {
                        var delta = target - run.State.Player(player).Life;
                        if (delta == 0)
                            continue;
                        run = Propose(run, new LifeChanged(player, delta, "setLife"), player);
                        if (run.Halted)
                            return run;
                    }

                    return run;
                }

            // CR 122.1: counters are put on and removed from objects one kind at a time. Putting
            // them on is offered to the replacement system first, CR 614.1: "with an additional
            // counter" and "enters with counters instead" are replacements of this event.
            case PutCounters put:
                {
                    var count = Number(run, put.N);
                    if (count <= 0)
                        return run;
                    foreach (var id in Objects(run, put.On))
                    {
                        if (!run.State.Objects.ContainsKey(id))
                            continue;
                        run = Propose(run, new CountersChanged(id, put.Counter, count), characteristics.ControllerOf(run.State, id));
                        if (run.Halted)
                            return run;
                    }

                    return run;
                }

            case RemoveCounters remove:
                {
                    foreach (var id in Objects(run, remove.From))
                    {
                        if (!run.State.Objects.TryGetValue(id, out var obj))
                            continue;
                        foreach (var kind in obj.Counters.Keys.Where(k => remove.Counter is null || k == remove.Counter).ToList())
                        {
                            var count = remove.All || remove.N is null ? obj.Counters[kind] : Number(run, remove.N);
                            run = Emit(run, new CountersChanged(id, kind, -count));
                        }
                    }

                    return run;
                }

            case PutPlayerCounters put:
                {
                    var count = Number(run, put.N);
                    foreach (var player in PlayersOrYou(run, put.Who))
                        run = Emit(run, new PlayerCountersChanged(player, put.Counter, count));
                    return run;
                }

            // CR 106.4: mana goes into the pool of the player the ability's controller names.
            case AddMana mana:
                {
                    var times = Math.Max(1, Number(run, mana.N));
                    var produced = 0;
                    foreach (var player in PlayersOrYou(run, mana.Who))
                    {
                        // CR 608.2d: each mana added is its own colour choice, read by its index.
                        for (var i = 0; i < times; i++)
                            run = Emit(run, new ManaChanged(player, ManaDelta(PoolFor(run, mana, i), mana, run.Context.Source, ref produced), "addMana"));
                    }

                    return run;
                }

            // CR 611.2: the effect is created with every number already worked out, because the
            // value is locked in as the spell resolves and never recalculated.
            case Cards.Apply apply:
                {
                    // CR 702.8: a permission granted to a player is created against that player
                    // however the card spelled the Apply; there is no object for it to reach.
                    if (Permitted(run, apply.What) is { } granted)
                        return granted.Count == 0 ? run : Create(run, apply.What, [], granted, apply.Until);

                    var targets = Objects(run, apply.To);
                    return targets.Count == 0 ? run : Create(run, apply.What, targets, [], apply.Until);
                }

            case ApplyToPlayer apply:
                {
                    var players = Permitted(run, apply.What) ?? PlayersOrYou(run, apply.Who);
                    return players.Count == 0 ? run : Create(run, apply.What, [], players, apply.Until);
                }

            // CR 500.7: an extra turn is taken after this one, in the order they were created.
            case Cards.ExtraTurn extra:
                {
                    var times = Math.Max(1, Number(run, extra.N));
                    foreach (var player in PlayersOrYou(run, extra.Who))
                        for (var i = 0; i < times; i++)
                            run = Emit(run, new ExtraTurnGranted(player));
                    return run;
                }

            // CR 104.2a and 104.3b: an effect can say a player wins or loses outright — unless a
            // continuous effect says that player can't. CR 101.2 gives the prohibition the last
            // word, so the instruction is not merely undone afterwards: nothing happens at all,
            // and no event is emitted for a game that did not end and a player who did not lose.
            case Cards.WinGame win:
                {
                    var winners = PlayersOrYou(run, win.Who)
                        .Where(p => !characteristics.PlayerRestrictions(run.State, p).CantWin)
                        .ToImmutableList();
                    return winners.Count == 0
                        ? run
                        : Emit(run, new GameEnded(new GameOutcome(winners, Draw: false, "effect")));
                }

            case Cards.LoseGame loseGame:
                {
                    foreach (var player in PlayersOrYou(run, loseGame.Who))
                    {
                        // CR 104.3b with 101.2 and 704.5a-c: a player who can't lose does not lose.
                        if (characteristics.PlayerRestrictions(run.State, player).CantLose)
                            continue;
                        run = Emit(run, new PlayerLost(player, "effect"));
                    }

                    return run;
                }

            // Looking and revealing change no public state in this slice; what they are for is the
            // memory a later step reads, and the per-player view, which is E14's.
            case Cards.LookAt look:
                return Recall(run, look.RememberAs, Objects(run, look.What));

            case Cards.Reveal reveal:
                return Recall(run, reveal.RememberAs, Objects(run, reveal.What));

            default:
                return null;
        }
    }

    /// <summary>
    /// CR 614.1a: gaining and losing life are offered to the replacement system before they
    /// happen, so "if you would gain life, gain twice that much" and "you can't lose life" are
    /// applied to the event rather than after it. A replacement that drops the event leaves no
    /// <see cref="LifeChanged"/> behind at all.
    /// </summary>
    [RuleRef("614.1a")]
    private EffectRun Life(EffectRun run, PlayerRef? who, int delta, string reason)
    {
        if (delta == 0)
            return run;
        foreach (var player in PlayersOrYou(run, who))
        {
            run = Propose(run, new LifeChanged(player, delta, reason), player);
            if (run.Halted)
                return run;
        }

        return run;
    }

    /// <summary>The source of damage: the selector when the card names one, otherwise the object the effect came from, CR 608.2.</summary>
    private ObjectId? SourceOf(EffectRun run, Selector? selector)
    {
        if (selector is null)
            return run.Context.Source;
        var named = Objects(run, selector);
        return named.Count > 0 ? named[0] : run.Context.Source;
    }

    private bool Has(GameState state, ObjectId id, KeywordKind keyword) =>
        state.Objects.ContainsKey(id) && characteristics.Of(state, id).Has(keyword);

    private EffectRun Create(
        EffectRun run,
        ContinuousEffect what,
        ImmutableList<ObjectId> objects,
        ImmutableList<PlayerId> players,
        Duration until)
    {
        var fixedUp = EffectWalk.Freeze(what, a => query.Evaluate(run.State, a, run.Context));
        var stamp = run.State.NextTimestamp;
        return Emit(run, new ContinuousEffectCreated(new ActiveEffect(
            stamp,
            fixedUp,
            run.Context.Source,
            run.Context.Controller,
            objects,
            players,
            until,
            stamp,
            FromStaticAbility: false,
            // CR 611.2c with 601.2c: the objects the effect's selectors named, fixed as it resolved, so the
            // layer system reads a Target or a remembered key from the binding and never from the board.
            Remembered: run.Context.Remembered)));
    }

    /// <summary>
    /// What one mana instruction produces, CR 106.1. Printed mana is read from the symbols; a
    /// colour chosen earlier comes out of the context. Anything else needs the mana subsystem's
    /// choice and is wave 3's.
    /// </summary>
    /// <summary>
    /// The delta one "add mana" instruction makes, CR 106.6. <c>ManaSources</c> is the one place
    /// that turns produced mana into "a restricted block when the card said what it may be spent on
    /// (CR 106.6b), plus provenance naming the permanent that produced it", so mana the interpreter
    /// adds and mana a mana ability adds cannot remember different things. A keyed instruction is
    /// the one shape that wants more than a block per colour: CR 106.6a creates a separate delayed
    /// ability for each mana produced, so its mana is asked for one at a time under
    /// <c>"key#n"</c> — through the same helper, so there is no second spelling of a block.
    /// </summary>
    [RuleRef("106.6")]
    private static ManaPool ManaDelta(ManaPool pool, AddMana mana, ObjectId? source, ref int produced)
    {
        var added = Mana.ManaSources.Added(pool, mana.SpendOnlyOn, source, mana.Key);
        if (mana.Key is not { } key || source is not { } from)
            return added;

        var blocks = ImmutableList.CreateBuilder<Mana.ManaProvenance>();
        foreach (var (colour, amount) in Mana.ManaSymbols.Amounts(pool))
        {
            for (var i = 0; i < amount; i++)
                blocks.AddRange(Mana.ManaSources.Produced(Mana.ManaSymbols.OfColor(colour), from, ManaKey(key, produced++)));
        }

        return added with { Provenance = blocks.ToImmutable() };
    }

    /// <summary>The key one mana of a keyed instruction carries, CR 106.6a: the card's key and which mana it is.</summary>
    [RuleRef("106.6a")]
    internal static string ManaKey(string key, int index) =>
        key + "#" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// What the <paramref name="index"/>th mana of one instruction is, CR 106.1: printed mana from
    /// the symbols; a keyed colour from what was chosen earlier (CR 608.2d); "any color", "one of"
    /// a set, or the chosen colour with no key from the letters the activation announced, which
    /// the stack entry carries under the mana key (CR 605.1a: an ability that adds mana and does
    /// more still resolves to its controller's choice). A set of one colour needs no announcement.
    /// </summary>
    [RuleRef("608.2d")]
    private ManaPool PoolFor(EffectRun run, AddMana mana, int index)
    {
        var production = mana.Mana;
        if (production.Fixed is { } symbols)
            return ManaSymbols.Parse(symbols);
        // CR 106.12a with 608.2d: a key alone names the chosen colour (the spelling the trigger system
        // and the vocabulary document); the ChosenColor flag beside it is not required.
        if (production.ChosenColorKey is { } key && run.Context.Chosen.TryGetValue(key, out var colour))
            return ManaSymbols.OfColor(colour);
        if (production.ChosenColorKey is { } missing)
            throw new Engine.RefusedException($"No colour was chosen under '{missing}' for this mana.", "608.2d");
        if (production.AnyColor || production.ChosenColor || production.OneOf is not null)
        {
            run.Context.Chosen.TryGetValue(Mana.ManaSymbols.AnnouncedColorsKey, out var announced);
            var announcedColour = Mana.ManaSymbols.ColorAt(announced, index)
                ?? (production.OneOf is { } only && Mana.ManaSymbols.IsSingleColor(only) ? (Color?)only : null);
            return announcedColour is { } chosenColour
                ? Mana.ManaSymbols.OfColor(chosenColour)
                : throw new Engine.RefusedException("The colour of mana to add was not chosen.", "608.2d");
        }

        throw new Engine.RefusedException($"No interpreter arm for this shape of {nameof(ManaProduction)}.", "608.2");
    }
}
