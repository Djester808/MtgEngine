using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Query;

/// <summary>
/// Numbers a card refers to. Counted when the effect asks, CR 608.2 — never cached, because a
/// count taken a step earlier is a different number.
/// </summary>
public sealed partial class Query
{
    public int Evaluate(GameState state, Amount amount, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(amount);
        return amount switch
        {
            Fixed fixedAmount => fixedAmount.N,
            X => context.X ?? 0, // CR 107.3b: X is zero unless a value was chosen for it
            CountOf count => Candidates(state, count.Filter, context).Count(),
            PlayerCount players => Players(state, players.Who, context).Count,
            LifeOf life => Players(state, life.Who, context).Sum(p => state.Player(p).Life), // CR 119.1
            CardsInHandOf hand => Players(state, hand.Who, context)
                .Sum(p => state.In(ZoneKey.Of(Cards.Zone.Hand, p)).Count), // CR 402.1
            CardsInZoneOf zone => CardsIn(state, zone.Who, zone.Zone, zone.Matching, context),

            PowerOf power => Resolve(state, power.Of, context).Sum(id => Known(state, id)?.Characteristics.Power ?? 0), // CR 208.1
            ToughnessOf toughness => Resolve(state, toughness.Of, context).Sum(id => Known(state, id)?.Characteristics.Toughness ?? 0),
            ManaValueOf manaValue => Resolve(state, manaValue.Of, context)
                .Sum(id => Known(state, id) is { } known ? ManaCosts.Value(known.Characteristics.ManaCost, known.Object) : 0), // CR 202.3

            CountersOn counters => Resolve(state, counters.On, context).Sum(id => CountersOf(state, id, counters.Counter)), // CR 122.1
            CountersOnPlayer counters => Players(state, counters.Who, context)
                .Sum(p => state.Player(p).Counters.GetValueOrDefault(counters.Counter)),

            Cards.Devotion devotion => DevotionOf(state, devotion, context), // CR 700.5
            Domain domain => BasicLandTypes(state, domain.Who, context),

            GreatestAmong greatest => Among(state, greatest.Among, greatest.Of, context) is { Count: > 0 } values
                ? values.Max() : 0,
            TotalAmong total => Among(state, total.Among, total.Of, context).Sum(),

            ChosenNumber chosen => ChosenNumberOf(chosen.Key, context),
            HighestChosen highest => SecretExtreme(nameof(HighestChosen), highest.Key, context, lowest: false), // CR 700.2
            LowestChosen lowest => SecretExtreme(nameof(LowestChosen), lowest.Key, context, lowest: true), // CR 700.2
            Stored stored => context.Stored.GetValueOrDefault(stored.Key),
            TurnNumber => state.Turn.Number, // CR 500.1

            CardsDrawnThisTurn drawn => PerTurn(state, drawn.Who, context, state.CardsDrawnThisTurn), // CR 121.1
            LandsEnteredThisTurn entered => PerTurn(state, entered.Who, context, state.LandsEnteredThisTurn), // CR 121.1

            Sum sum => Evaluate(state, sum.Left, context) + Evaluate(state, sum.Right, context),
            Difference difference => Evaluate(state, difference.Left, context) - Evaluate(state, difference.Right, context),
            Times times => Evaluate(state, times.Value, context) * times.Factor,
            // CR 107.2: a fraction rounds down unless the effect says otherwise.
            Cards.Half half => half.RoundUp
                ? (Evaluate(state, half.Value, context) + 1) / 2
                : Evaluate(state, half.Value, context) / 2,
            Max max => Math.Max(Evaluate(state, max.Left, context), Evaluate(state, max.Right, context)),
            Min min => Math.Min(Evaluate(state, min.Left, context), Evaluate(state, min.Right, context)),

            ManaSpentOnThis spent => ManaSpent(state, spent.OfColor, context), // CR 601.2h
            Cards.TimesKicked => KicksOfSource(state, context), // CR 702.32b
            SpellsCastThisTurn cast => SpellsCast(state, cast, context), // CR 603.10
            DamageDealtThisTurn damage => DamageThisTurn(state, damage, context), // CR 120.3
            TriggeringDamage => TriggeringDamageOf(context), // CR 603.10
            TriggeringLifeGained => CarriedNumber(context, TriggeringLifeGainedAmount), // CR 603.10
            TriggeringLifeLost => CarriedNumber(context, TriggeringLifeLostAmount), // CR 603.10
            // CR 615.13: what a prevention prevented, and never the dealt key — a shield that stopped
            // 2 of 5 leaves 3 dealt, and one that stopped all of it leaves no damage event at all.
            TriggeringPreventedDamage => CarriedNumber(context, TriggerSystem.TriggeringPreventedDamageAmount),
            ManaSpentOnTriggering mana => ManaSpentOnTriggeringSpell(mana.OfColor, context), // CR 400.7d
            Cards.PreventedDamage => PreventedDamageOf(context), // CR 615
            GreatestDamageDealtThisTurn worst => GreatestDamageTo(state, worst, context), // CR 102.2
            CreaturesDiedThisTurn died => CreaturesDied(state, died, context), // CR 700.4
            GreatestSameNameCount sameName => GreatestSameName(state, sameName, context), // CR 201.2
            AttackersAgainst against => AttackersAgainstPlayers(state, against, context), // CR 508.1
            DistinctNameCount names => DistinctNames(state, names, context), // CR 201.2b
            // Life loss still needs a complete per-player record; damage alone cannot answer it.
            LifeLostThisTurn => throw NoLifeLostThisTurn(), // CR 119.3
            CommanderCastsThisGame casts => CommanderCastCount(state, casts, context), // CR 903.8
            _ => throw Unanswerable(amount.GetType().Name, "no arm in the query layer"),
        };
    }

    /// <summary>Counters of one kind, or every counter when the amount names none, CR 122.1.</summary>
    private static int CountersOf(GameState state, ObjectId id, string? counter)
    {
        var obj = state.Objects[id];
        return counter is null ? obj.Counters.Values.Sum() : obj.Counters.GetValueOrDefault(counter);
    }

    /// <summary>
    /// Devotion, CR 700.5: mana symbols of the given colors among the mana costs of permanents the
    /// player controls. When the card chose the colour rather than printing it, the choice made
    /// earlier in the same effect is the colour counted — "your devotion to the chosen color" is a
    /// different card from "your devotion to black", so the printed field is never used for it.
    /// </summary>
    [RuleRef("700.5")]
    private int DevotionOf(GameState state, Cards.Devotion devotion, EffectContext ctx)
    {
        var colors = devotion.Colors;
        if (devotion.ChosenColorKey is { } key)
        {
            colors = ctx.Chosen.TryGetValue(key, out var chosen) && ColorNamed(chosen) is { } named
                ? named
                : throw Unanswerable(nameof(Cards.Devotion), $"no color was chosen for \"{key}\"", "700.5");
        }

        return Candidates(state, new ObjectFilter(ControlledBy: devotion.Who), ctx)
            .Sum(id => ManaCosts.Devotion(_layers.Of(state, id).ManaCost, colors));
    }

    /// <summary>Mana spent to cast the spell this effect is on, CR 601.2h; with no colour named, every symbol paid.</summary>
    [RuleRef("601.2h")]
    private static int ManaSpent(GameState state, Color? ofColor, EffectContext ctx)
    {
        var spent = Spent(state, ctx);
        return ofColor is { } color ? spent.GetValueOrDefault(color) : spent.Values.Sum();
    }

    /// <summary>
    /// Spells cast this turn, CR 603.10: the turn's record holds who cast each spell and from
    /// where. A filter is applied to the spell object, which only still exists while it is on the
    /// stack — a resolved spell is a different object (CR 400.7), so a filtered count is a count
    /// of what is still castable-or-on-the-stack and the plain count is the honest one.
    /// </summary>
    [RuleRef("603.10")]
    private int SpellsCast(GameState state, SpellsCastThisTurn cast, EffectContext ctx)
    {
        var casters = Players(state, cast.By, ctx);
        return state.History.SpellsCast.Count(record => casters.Contains(record.By)
            && (cast.Matching is not { } filter
                || (state.Objects.ContainsKey(record.Spell) && Matches(state, filter, record.Spell, ctx))));
    }

    /// <summary>
    /// Damage dealt this turn, CR 120.3. The turn's history totals it per source and per receiver,
    /// so "damage dealt by these sources" and "damage dealt to that player" are answerable from the
    /// totals; the pair of the two needs the per-pair map, and refuses without it rather than
    /// answering a different question. See seam-requests/E0c.md.
    /// </summary>
    [RuleRef("120.3")]
    private int DamageThisTurn(GameState state, DamageDealtThisTurn damage, EffectContext ctx)
    {
        if (damage is { By: { } source, To: { } receiver })
            return DamagePairTotal(state, source, receiver, ctx);
        if (damage.By is { } by)
            return Resolve(state, by, ctx).Sum(id => state.History.DamageDealtBy.GetValueOrDefault(id));
        if (damage.To is { } to)
            return Players(state, to, ctx).Sum(p => state.History.DamageReceivedByPlayer.GetValueOrDefault(p));
        return state.History.DamageDealtBy.Values.Sum();
    }

    /// <summary>
    /// The damage the event that fired this ability dealt, CR 603.10. The trigger system writes it
    /// into the firing's choices under <see cref="Triggers.TriggerSystem.TriggeringDamageAmount"/>,
    /// culture-invariantly, and it travels to the resolution's context from there, so reading that
    /// key is the whole answer — nothing else in the state remembers one event's damage. A copy of
    /// such an ability made outside a firing carries no key, and reads zero rather than refusing:
    /// no damage was dealt to the ability, which is what the number then stands for.
    /// </summary>
    [RuleRef("603.10")]
    private static int TriggeringDamageOf(EffectContext ctx) =>
        CarriedNumber(ctx, Triggers.TriggerSystem.TriggeringDamageAmount);

    /// <summary>
    /// The key the life <em>gained</em> by the firing event is carried under, CR 603.10, spelled
    /// the way <c>TriggerSystem.TriggeringDamageAmount</c> is. The trigger system has no constant
    /// for it yet — the wave-26 trigger box owns that side — so the string is written here rather
    /// than guessed at each call, and seam-requests/E5k.md § 3 asks for the constant and the write
    /// so the two ends cannot drift silently. Life gained is not damage and is not life lost: three
    /// events, three keys, and no reader falls back to another (CR 118.3 with 119.3).
    /// </summary>
    [RuleRef("603.10")]
    public const string TriggeringLifeGainedAmount = TriggerSystem.TriggeringLifeGainedAmount;

    /// <summary>
    /// The key the life <em>lost</em> by the firing event is carried under, CR 603.10, the mirror
    /// of <see cref="TriggeringLifeGainedAmount"/>. A payment, an edict's cost and a burn spell all
    /// lose life and only one of them is damage (CR 118.3), so this never reads the damage key.
    /// </summary>
    [RuleRef("603.10")]
    public const string TriggeringLifeLostAmount = TriggerSystem.TriggeringLifeLostAmount;

    /// <summary>
    /// A number the firing carried into the resolution's context, CR 603.10: written by the trigger
    /// system as decimal, culture-invariant text under its own key, because the board no longer
    /// says it once the event is over. A copy of such an ability made outside a firing carries no
    /// key and reads zero rather than refusing — no life was gained or lost and no damage was dealt
    /// to the ability, which is exactly what the number then stands for.
    /// </summary>
    [RuleRef("603.10")]
    private static int CarriedNumber(EffectContext ctx, string key) =>
        ctx.Chosen.TryGetValue(key, out var text)
        && int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    /// <summary>
    /// The attackers of the current declaration aimed at a player, CR 508.1. The combat state holds
    /// the declaration for the step, so this is read as the ability's intervening "if" is checked —
    /// once as the trigger goes on the stack and again on resolution, CR 603.4 — and a creature
    /// removed from combat between the two rightly makes the second reading smaller. A creature
    /// attacking a planeswalker or battle that player controls is attacking their side of the board
    /// (CR 506.1), so it counts while the flag stands; a departed defender is read as last-known
    /// information (CR 608.2h), because a planeswalker that died is still what the attack was aimed
    /// at.
    /// </summary>
    [RuleRef("508.1")]
    private int AttackersAgainstPlayers(GameState state, AttackersAgainst against, EffectContext ctx)
    {
        if (state.Combat is not { } combat)
            return 0;

        var defenders = Players(state, against.Who, ctx);
        return combat.Attackers.Values.Count(target =>
            (target.Player is { } player && defenders.Contains(player))
            || (against.IncludingPermanentsTheyControl
                && target.Permanent is { } permanent
                && DefendsFor(state, permanent, defenders)));
    }

    /// <summary>CR 506.1: a planeswalker or battle one of those players controls, the other thing an attack can be aimed at.</summary>
    [RuleRef("506.1")]
    private bool DefendsFor(GameState state, ObjectId permanent, ImmutableList<PlayerId> defenders) =>
        Known(state, permanent) is { } known
        && (known.Characteristics.Types & (CardType.Planeswalker | CardType.Battle)) != CardType.None
        && defenders.Contains(known.Characteristics.Controller);

    /// <summary>Domain: the basic land types among lands the player controls, CR 205.3i.</summary>
    private int BasicLandTypes(GameState state, PlayerRef who, EffectContext ctx)
    {
        var lands = Candidates(state, new ObjectFilter(Types: CardType.Land, ControlledBy: who), ctx)
            .Select(id => _layers.Of(state, id))
            .ToList();
        return LandTypes.Basic.Count(basic => lands.Any(land => HasSubtype(land, basic)));
    }

    /// <summary>
    /// "the greatest power among creatures you control": the inner amount is evaluated once per
    /// matching object, with that object as the one the amount speaks of, CR 608.2.
    /// </summary>
    private List<int> Among(GameState state, ObjectFilter among, Amount of, EffectContext ctx) =>
        [.. Candidates(state, among, ctx).Select(id => Evaluate(state, of, ctx with { Source = id }))];

    /// <summary>A number chosen earlier: stored as a count when it is one, else as the text of the choice.</summary>
    private static int ChosenNumberOf(string key, EffectContext ctx)
    {
        if (ctx.Stored.TryGetValue(key, out var stored))
            return stored;
        if (ctx.Chosen.TryGetValue(key, out var text)
            && int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw Unanswerable(nameof(ChosenNumber), $"nothing was chosen for \"{key}\"");
    }

    /// <summary>
    /// A number a turn keeps per player, CR 121.1, read over the seats the reference admits: the
    /// greatest of them, never their sum. Both amounts exist for a printed line about <em>one</em>
    /// player — "any opponent drew two or more cards this turn" asks whether some single opponent
    /// reached the number, and a sum would answer yes when two opponents drew one card each. With
    /// one seat admitted, which is every "you" reading, the two folds are the same number; with
    /// none, nobody drew and nobody played a land, so it is zero rather than a refusal.
    /// <para>
    /// It is the one place a per-player amount does not sum. <see cref="LifeOf"/> and
    /// <see cref="CardsInHandOf"/> total their seats because "the total life your opponents have
    /// lost" is a printed line and no card asks whether any one opponent drew a total; a card that
    /// wanted the total of these would want a record that says so (seam-requests/E5j.md).
    /// </para>
    /// </summary>
    [RuleRef("121.1")]
    private int PerTurn(GameState state, PlayerRef who, EffectContext ctx, Func<PlayerId, int> of)
    {
        var seats = Players(state, who, ctx);
        return seats.Count == 0 ? 0 : seats.Max(of);
    }

    /// <summary>
    /// Every number one secret simultaneous choice collected, CR 700.2 with 101.4, with the seat
    /// that chose it. The interpreter is the only writer: it asks each player in turn order and
    /// remembers the answer under <see cref="Interpreter.EffectInterpreter.SecretNumberKey"/>, so
    /// the readers look under exactly the prefix that arm writes and never spell a key of their own.
    /// Nothing is written until a player has answered, which is what makes "no entry" mean "nobody
    /// has chosen" rather than "somebody chose nothing".
    /// <para>
    /// The record cites CR 700.2, which in the shipped rules text is the modal rule; the rule that
    /// actually governs simultaneous secret choices is CR 101.4, and the divergence is recorded in
    /// seam-requests/E5j.md rather than fixed here, because the number is the vocabulary's and the
    /// interpreter's too.
    /// </para>
    /// </summary>
    [RuleRef("700.2")]
    private static ImmutableList<(PlayerId Player, int Number)> SecretNumbers(string key, EffectContext ctx)
    {
        var prefix = Interpreter.EffectInterpreter.SecretNumbersUnder(key);
        var numbers = ImmutableList.CreateBuilder<(PlayerId Player, int Number)>();
        foreach (var (chosen, text) in ctx.Chosen)
        {
            if (!chosen.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            if (int.TryParse(chosen[prefix.Length..], System.Globalization.CultureInfo.InvariantCulture, out var seat)
                && int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                numbers.Add((new PlayerId(seat), number));
            }
        }

        return numbers.ToImmutable();
    }

    /// <summary>
    /// "the highest number chosen", CR 700.2 with 101.4: the greatest or least of the revealed
    /// numbers. A key nobody answered is refused rather than read as zero — zero is a number a
    /// player could have chosen, so answering it would make a card that damages each player by the
    /// highest number deal none and look as though the game had played the card.
    /// </summary>
    [RuleRef("700.2")]
    private static int SecretExtreme(string record, string key, EffectContext ctx, bool lowest)
    {
        var numbers = SecretNumbers(key, ctx);
        if (numbers.Count == 0)
            throw NoSecretNumbers(record, key);

        return lowest ? numbers.Min(n => n.Number) : numbers.Max(n => n.Number);
    }

    /// <summary>The one refusal both readers of a secret choice give, CR 700.2.</summary>
    private static RefusedException NoSecretNumbers(string record, string key) =>
        Unanswerable(record, $"no secret number was revealed under \"{key}\", so there is no highest or lowest to read", "700.2");

    /// <summary>
    /// The characteristics an amount reads off one object, CR 208.1 and 202.3: the object as it is
    /// now, or, when it has left the battlefield, as it last existed there — CR 608.2h with 400.7:
    /// an effect that reads a departed object uses last-known information rather than failing.
    /// Null when the game never held it, which counts for nothing.
    /// </summary>
    [RuleRef("608.2h")]
    private (Characteristics Characteristics, GameObject Object)? Known(GameState state, ObjectId id)
    {
        if (state.Objects.TryGetValue(id, out var here))
            return (_layers.Of(state, id), here);
        return LastKnown(state, id) is { } gone ? (_layers.PrintedOf(gone), gone) : null;
    }
}
