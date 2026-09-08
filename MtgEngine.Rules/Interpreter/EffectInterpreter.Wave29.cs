using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// Wave 29: the three records the vocabulary had written and no arm read — a discard that names
/// what may be discarded and who picks it (CR 701.9b), a token whose size is an amount evaluated
/// as it is created (CR 111.3), and the per-permanent-type edict (CR 701.21 with 110.4).
/// </summary>
public sealed partial class EffectInterpreter
{
    /// <summary>CR 110.4: the six permanent types, in the order the rule prints them.</summary>
    [RuleRef("110.4")]
    private static readonly CardType[] PermanentKinds =
    [
        CardType.Artifact,
        CardType.Battle,
        CardType.Creature,
        CardType.Enchantment,
        CardType.Land,
        CardType.Planeswalker,
    ];

    /// <summary>
    /// CR 701.9a with 701.9b. A discard moves a card from its owner's hand to their graveyard;
    /// <b>who picks it</b> is the record's <c>By</c>, and the default is the affected player's own
    /// choice. A record that says otherwise means a random discard — no question at all, the card
    /// comes off the seeded generator in the state so the same log replays the same discard — or
    /// another player choosing, which is the same question asked of a different seat.
    /// <para>
    /// <c>What</c> narrows the hand before any of that: "discard a creature card" may only take a
    /// creature card, so a hand holding none discards nothing rather than asking a question no
    /// answer could satisfy (CR 101.4).
    /// </para>
    /// <para>
    /// Which player the walk had reached rides on the question as a loop mark rather than being
    /// recovered from the answer's own seat: with <c>By: Controller</c> the seat that answers is
    /// not the seat that discards, and reading the answerer's index would restart the loop at the
    /// wrong player (CR 101.4).
    /// </para>
    /// </summary>
    [RuleRef("701.9")]
    private EffectRun Discarding(EffectRun run, Cards.Discard discard)
    {
        var discarding = PlayersOrYou(run, discard.Who);
        var asked = Marked(run) is { Count: > 0 } mark ? mark[0] : -1;

        // CR 701.9a: the cards the seat that was asked chose are discarded, and the players after
        // the player they chose for are reached in their turn.
        if (Answered(run) is { } discarded)
            run = Unmark(Leave(run, Named(run.State, discarded), "discarded", Zone.Graveyard, skipIndestructible: false));

        for (var i = asked + 1; i < discarding.Count; i++)
        {
            var player = discarding[i];
            var hand = Discardable(run, player, discard.What);
            if (discard.WholeHand)
            {
                run = Leave(run, hand, "discarded", Zone.Graveyard, skipIndestructible: false);
                continue;
            }

            var count = Math.Min(hand.Count, Math.Max(0, Number(run, discard.N)));
            if (count == 0)
                continue;

            // CR 701.9b: a random discard is not a choice, so the walk does not stop; it takes the
            // cards off the state's own generator, the way ZoneSystem.Shuffle does, and logs the
            // advance so a replay of the log discards the same cards.
            if (discard.By == DiscardChooser.Random)
            {
                run = AtRandom(run, hand, count);
                continue;
            }

            // CR 701.9b: "another player chooses" is the affected player's discard answered from a
            // different seat, so the cards still leave this player's hand.
            var chooser = discard.By == DiscardChooser.Controller ? run.Context.Controller : player;
            return Ask(
                Mark(run, [i]),
                chooser,
                ChoiceKinds.Discard,
                "Choose what to discard.",
                OptionsFor(run.State, hand),
                count,
                count);
        }

        return Unmark(run);
    }

    /// <summary>
    /// The cards in this player's hand a discard may take, CR 701.9a with 701.9b: the record's
    /// filter is read against the hand before the count is, so "discard a land card" from a hand
    /// with no land discards nothing instead of taking whatever is there.
    /// </summary>
    [RuleRef("701.9a")]
    private ImmutableList<ObjectId> Discardable(EffectRun run, PlayerId player, ObjectFilter? what)
    {
        var hand = run.State.In(ZoneKey.Of(Zone.Hand, player));
        if (what is null)
            return hand;

        // CR 701.9a: a discarded card is in a hand by definition, so the zone a filter with no
        // zone of its own would default to (the battlefield) is not the one being read here.
        var inHand = what with { In = Zone.Hand };
        return [.. hand.Where(id => query.Matches(run.State, inHand, id, run.Context))];
    }

    /// <summary>
    /// CR 701.9b: a random discard. The cards are drawn one at a time from the seeded generator on
    /// the state (CR 104 has nothing to say about it; the reproducibility is this engine's own
    /// rule, the same one <c>ZoneSystem.Shuffle</c> keeps), and each draw is logged as an
    /// <see cref="RngAdvanced"/> before the card moves, so the log replays the same discard.
    /// </summary>
    [RuleRef("701.9b")]
    private EffectRun AtRandom(EffectRun run, ImmutableList<ObjectId> hand, int count)
    {
        var pool = hand.ToList();
        for (var i = 0; i < count && pool.Count > 0; i++)
        {
            var (at, next) = Rng.Below(run.State.Rng, pool.Count);
            run = Emit(run, new RngAdvanced(next));
            run = Leave(run, [pool[at]], "discarded", Zone.Graveyard, skipIndestructible: false);
            pool.RemoveAt(at);
        }

        return run;
    }

    /// <summary>
    /// CR 111.3: the ability that creates a token defines the values of its characteristics, and
    /// those values are the token's own text — its copiable values. So "create an X/X token, where
    /// X is …" is evaluated <b>as the token is created</b> and written into the spec, never applied
    /// as a setting afterwards: a layer-7b setting is not copiable, a second setting would overwrite
    /// it, and for the moment before it applied the token would be a 0/0 that CR 704.5f puts into
    /// the graveyard.
    /// </summary>
    [RuleRef("111.3")]
    private TokenSpec Sized(EffectRun run, TokenSpec token) =>
        token.PowerAmount is null && token.ToughnessAmount is null
            ? token
            : token with
            {
                Power = token.PowerAmount is { } power ? Number(run, power, whenMissing: 0) : token.Power,
                Toughness = token.ToughnessAmount is { } toughness ? Number(run, toughness, whenMissing: 0) : token.Toughness,
            };

    /// <summary>
    /// CR 701.21 with 110.4: "each player chooses a permanent they control of each permanent type,
    /// then sacrifices the rest". The six permanent types are artifact, battle, creature,
    /// enchantment, land and planeswalker, and one permanent may have two of them — an artifact
    /// creature is kept by either choice and answers both — so the kept set can be smaller than the
    /// number of types the player controls and the count asked for is a maximum, never an exact
    /// number (CR 205.2b).
    /// <para>
    /// CR 101.4: every player named chooses before anything is sacrificed, and the sacrifices then
    /// happen at once. A loop of one edict per player would let the first player's triggers change
    /// the board the second chooses from, so the kept objects ride on the question as a mark and
    /// nothing leaves the battlefield until the last seat has answered.
    /// </para>
    /// </summary>
    [RuleRef("701.21")]
    private EffectRun KeepingOnePerType(EffectRun run, Sacrifice sacrifice)
    {
        var sacrificing = InTurnOrder(run.State, PlayersOrYou(run, sacrifice.Who));
        var progress = Marked(run) ?? [];
        var asked = progress.Count > 0 ? progress[0] : -1;
        var kept = progress.Skip(1).Select(v => new ObjectId(v)).ToList();
        if (Answered(run) is { } given)
            kept.AddRange(Named(run.State, given));

        for (var i = asked + 1; i < sacrificing.Count; i++)
        {
            var mine = Controlled(run.State, sacrificing[i], sacrifice.Choice, run.Context);

            // CR 701.21a: a player who controls nothing the effect admits keeps and sacrifices
            // nothing, and is not asked a question no answer could satisfy (CR 101.4).
            var types = PermanentTypes(run.State, mine);
            if (types == 0)
                continue;

            return Ask(
                Mark(run, [i, .. kept.Select(k => k.Value)]),
                sacrificing[i],
                ChoiceKinds.Sacrifice,
                "Choose a permanent you control of each permanent type; you sacrifice the rest.",
                OptionsFor(run.State, mine),
                1,
                Math.Min(mine.Count, types));
        }

        // CR 101.4: everyone has chosen, so every permanent not kept is sacrificed simultaneously.
        var doomed = sacrificing
            .SelectMany(player => Controlled(run.State, player, sacrifice.Choice, run.Context))
            .Where(id => !kept.Contains(id));
        return Unmark(Leave(run, doomed, "sacrificed", Zone.Graveyard, skipIndestructible: false));
    }

    /// <summary>
    /// How many of CR 110.4's six permanent types these permanents cover between them: the most a
    /// player could have to keep, and zero when they control nothing the effect admits.
    /// </summary>
    [RuleRef("110.4")]
    private int PermanentTypes(GameState state, IEnumerable<ObjectId> permanents)
    {
        var seen = CardType.None;
        foreach (var id in permanents)
            seen |= characteristics.Of(state, id).Types;

        return PermanentKinds.Count(kind => seen.HasFlag(kind));
    }
}
