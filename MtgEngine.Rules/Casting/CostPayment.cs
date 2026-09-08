using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// Everything one payment needs to know, CR 601.2h: whose it is, what the source of the cost is,
/// the X that was announced, and the restriction CR 106.6b puts on which mana may be spent.
/// </summary>
[RuleRef("601.2h")]
internal sealed record CostContext(
    GameObject? Source,
    Characteristics? Characteristics,
    PlayerId Player,
    int? X,
    IZoneSystem Zones,
    IQuery Query,
    Color? PayOnlyWith = null,
    ChosenObjects? Chosen = null,
    ObjectFilter? PayOnlyFrom = null,
    bool PayPhyrexianWithLife = false,
    ImmutableList<string>? Choices = null)
{
    /// <summary>
    /// The word a command's <c>CostChoices</c> carries to announce CR 107.4f's other half: every
    /// Phyrexian symbol in this cost is paid with 2 life rather than with mana. It is announced
    /// with the command like every other cost choice (CR 601.2h), so no payment is suspended; a
    /// player who does not say it pays the colour whenever the pool has it.
    /// </summary>
    [RuleRef("107.4f")]
    public const string PayWithLife = "life";

    /// <summary>
    /// The word a command's <c>CostChoices</c> carries to take one of the costs a
    /// <see cref="ChooseCost"/> offers, CR 601.2b: <c>"cost"</c> is the first option as printed and
    /// <c>"cost:1"</c> the second. It rides the same list every other cost choice does, so the
    /// choice is made as the spell or ability is announced and no payment is ever suspended to ask.
    /// </summary>
    [RuleRef("601.2b")]
    public const string ChooseCostChoice = "cost";

    /// <summary>The context an <see cref="Amount"/> or an <see cref="ObjectFilter"/> on a cost is read in.</summary>
    public EffectContext Effect => CostPayment.Context(Source?.Id, Player, X);

    /// <summary>The objects the command named for these costs, CR 601.2c; none when it named none.</summary>
    public ChosenObjects Named => Chosen ?? ChosenObjects.None;
}

/// <summary>
/// Paying a cost, CR 601.2h: every cost is paid in full or the action is refused, because partial
/// payments are not allowed. Mana, the tap symbol and the costs a source pays with itself are
/// here; the costs that name other objects are <see cref="ObjectCosts"/>. Any cost neither can
/// pay is refused naming itself, so nothing is ever half paid and the backlog gets the primitive.
/// </summary>
[RuleRef("601.2h")]
internal static class CostPayment
{
    /// <summary>Pay all of the costs, in the order written. Throws <see cref="RefusedException"/> naming the rule that stops it.</summary>
    public static Outcome Pay(GameState state, CostContext context, IReadOnlyList<Cost> costs)
    {
        ArgumentNullException.ThrowIfNull(context);
        var events = ImmutableList.CreateBuilder<GameEvent>();
        foreach (var cost in Flatten(costs))
        {
            var step = One(state, context, cost);
            events.AddRange(step.Events);
            state = step.State;
        }

        return new Outcome(state, events.ToImmutable());
    }

    /// <summary>A mana cost, parsed and spent out of the player's pool, CR 601.2h.</summary>
    public static Outcome PayMana(GameState state, PlayerId player, string? printed, int? x, string reason, Color? only = null) =>
        PaySymbols(state, player, ManaSymbols.Parse(printed), x, reason, only).Outcome;

    /// <summary>
    /// An already-parsed cost spent out of the pool, CR 601.2h, with what was actually spent
    /// handed back: CR 202.3a's "mana spent to cast this spell" is a property of the spell, so
    /// the caller records it on the stack entry rather than recomputing it later.
    /// <paramref name="only"/> is CR 106.6b's spend restriction: when it is set, only mana of
    /// those colours may pay, and the refusal names that rule rather than an empty pool.
    /// </summary>
    [RuleRef("601.2h")]
    public static PaidMana PaySymbols(GameState state, PlayerId player, IReadOnlyList<ManaSymbol> symbols, int? x, string reason, Color? only = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Spend(state, player, state.Player(player).Mana, symbols, x, reason, only);
    }

    /// <summary>
    /// The same payment made for a known spell or ability, so CR 106.6a's restricted mana can be
    /// offered to it: only the blocks whose filter matches what is being paid for are spendable,
    /// and ordinary mana is always. <see cref="RestrictedPool"/> reads the blocks off the pool.
    /// CR 106.6b's other half is applied here too: a <see cref="CostContext.PayOnlyFrom"/> filter
    /// narrows the spendable pool to the mana whose provenance names a permanent it matches, so
    /// the payment can never reach for mana the ability does not accept.
    /// </summary>
    [RuleRef("106.6a")]
    public static PaidMana PaySymbols(GameState state, CostContext context, IReadOnlyList<ManaSymbol> symbols, string reason)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        var held = state.Player(context.Player).Mana.Provenance;
        var offer = RestrictedPool.Pools.Offer(
            state,
            state.Player(context.Player).Mana,
            context.Query,
            context.Source?.Id,
            context.Effect);
        var spendable = context.PayOnlyFrom is { } from
            ? ManaSources.From(offer.Pool, held, id => state.Objects.ContainsKey(id) && context.Query.Matches(state, from, id, context.Effect))
            : offer.Pool;
        return Spend(state, context.Player, spendable, symbols, context.X, reason, context.PayOnlyWith, offer.Offered, held, context.PayOnlyFrom is not null, context.PayPhyrexianWithLife);
    }

    private static PaidMana Spend(
        GameState state,
        PlayerId player,
        ManaPool pool,
        IReadOnlyList<ManaSymbol> symbols,
        int? x,
        string reason,
        Color? only,
        ImmutableList<RestrictedBlock>? offered = null,
        ImmutableList<ManaProvenance>? held = null,
        bool onlyFromSource = false,
        bool phyrexianWithLife = false)
    {
        // CR 107.4f with 119.4: the life half of a Phyrexian symbol is capped by the life the
        // payer actually has, so the payer's total is what the plan is allowed to spend.
        var payment = ManaSymbols.Pay(
            only is null ? pool : ManaSymbols.Only(pool, only.Value),
            symbols,
            x,
            state.Player(player).Life,
            phyrexianWithLife);
        if (payment.Refused is { } refusal)
        {
            // CR 106.6b: the rider is why the payment failed, so the refusal names that rule
            // rather than an empty pool — the mana is there, it simply may not pay this cost.
            if (onlyFromSource)
                throw new RefusedException("That cost must be paid with mana produced by what this ability names.", "106.6b");
            throw only is null
                ? new RefusedException(refusal.Reason, refusal.Rule)
                : new RefusedException("That cost must be paid with mana this ability accepts.", "106.6b");
        }

        // CR 107.4f: the life that paid the Phyrexian symbols leaves the payer as part of the same
        // payment, so it is one more event on the outcome and no mana is recorded as having paid.
        var life = payment.LifePaid > 0 ? new LifeChanged(player, -payment.LifePaid, reason) : null;
        if (payment.Delta.Total == 0)
        {
            return life is null
                ? new PaidMana(Outcome.Unchanged(state), ManaPool.Empty)
                : new PaidMana(new Outcome(life.Apply(state), [life]), ManaPool.Empty);
        }

        var spent = new ManaPool(
            -payment.Delta.White,
            -payment.Delta.Blue,
            -payment.Delta.Black,
            -payment.Delta.Red,
            -payment.Delta.Green,
            -payment.Delta.Colorless);

        // CR 106.6a: a spend comes out of the restricted block that was offered to it before it
        // comes out of the ordinary counts, so the block that paid is the block that empties.
        var delta = offered is { Count: > 0 } ? RestrictedPool.DeltaFor(spent, offered) : payment.Delta;

        // CR 106.6: the spend takes the provenance blocks along with the totals and reports them,
        // so a "when that mana is spent" trigger can read which mana actually paid.
        var changed = new ManaChanged(player, delta with { Provenance = ManaSources.Spend(held ?? [], spent) }, reason);
        var after = changed.Apply(state);
        if (life is null)
            return new PaidMana(new Outcome(after, [changed]), spent);
        return new PaidMana(new Outcome(life.Apply(after), [changed, life]), spent);
    }

    /// <summary>
    /// The mana spent, by colour, for <see cref="StackEntry.ManaSpent"/>. Colorless mana is
    /// <see cref="Color.Colorless"/>, CR 106.1b: there is no generic mana in a pool, so what paid
    /// a generic symbol is recorded as the colour it actually was.
    /// </summary>
    [RuleRef("202.3a")]
    public static ImmutableDictionary<Color, int> ByColor(ManaPool spent)
    {
        ArgumentNullException.ThrowIfNull(spent);
        var byColor = ImmutableDictionary.CreateBuilder<Color, int>();
        foreach (var (color, amount) in new[]
        {
            (Color.White, spent.White),
            (Color.Blue, spent.Blue),
            (Color.Black, spent.Black),
            (Color.Red, spent.Red),
            (Color.Green, spent.Green),
            (Color.Colorless, spent.Colorless),
        })
        {
            if (amount > 0)
                byColor[color] = amount;
        }

        return byColor.ToImmutable();
    }

    /// <summary>Several costs paid together are one list, CR 601.2f; a choice of costs is not in this slice.</summary>
    internal static IEnumerable<Cost> Flatten(IReadOnlyList<Cost> costs)
    {
        foreach (var cost in costs)
        {
            if (cost is Costs all)
            {
                foreach (var inner in Flatten([.. all.All]))
                    yield return inner;
            }
            else
            {
                yield return cost;
            }
        }
    }

    private static Outcome One(GameState state, CostContext context, Cost cost)
    {
        switch (cost)
        {
            case ManaCost mana:
                return PaySymbols(state, context, ManaSymbols.Parse(mana.Mana), "cost").Outcome;

            // CR 118.8: generic mana whose amount the game counts, evaluated as the cost is
            // announced (CR 601.2h) and paid alongside any printed coloured part.
            case PayManaCost counted:
                return PayCounted(state, context, counted);

            case TapSelf:
                return TapTheSource(state, context);

            case UntapSelf:
                return UntapTheSource(state, context);

            case SacrificeSelf:
                return Sacrifice(state, context);

            case DiscardSelfCost:
                return Discard(state, context);

            case PayLife life:
                return LoseLife(state, context.Player, context.Query.Evaluate(state, life.Amount, context.Effect));

            // CR 601.2b: "discard a card or pay 3 life" is one additional cost with a choice
            // inside it, and the choice is made as the spell is announced — before anything is
            // paid (CR 601.2h) — so the option is picked here and then paid like any other cost.
            case ChooseCost choose:
                return Pay(state, context, [Chosen(state, context, choose)]);

            default:
                // CR 601.2h: an unpayable cost can't be paid. Naming the cost is how the backlog
                // gets it: the primitive is missing, the action is not silently allowed.
                return ObjectCosts.Pay(state, context, cost)
                    ?? throw new RefusedException($"A {cost.GetType().Name} cost cannot be paid yet.", "601.2h");
        }
    }

    /// <summary>
    /// Which of the costs a <see cref="ChooseCost"/> offers is being paid, CR 601.2b. The command
    /// names it on <c>CostChoices</c> (<see cref="CostContext.ChooseCostChoice"/>); a command that
    /// names none takes the first option that can be paid, which is a real choice and so is made
    /// the one way that is the same on every replay — the printed order.
    /// <para>
    /// The trial that finds it is made against a copy of the objects the command named
    /// (<see cref="ChosenObjects.Rest"/>), so probing an option the payer will not take cannot move
    /// the position the costs after this one read from (CR 601.2c). Its state is discarded: only
    /// the chosen option is handed back, and the payment that follows is the only one that happens.
    /// </para>
    /// </summary>
    /// <exception cref="RefusedException">
    /// CR 601.2b when the command names an option the cost does not offer, CR 601.2h when none of
    /// the options can be paid — partial payments are not allowed, so nothing is half taken.
    /// </exception>
    [RuleRef("601.2b")]
    internal static Cost Chosen(GameState state, CostContext context, ChooseCost choose)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(choose);
        if (choose.Options.Count == 0)
            throw new RefusedException("That cost offers nothing to choose between.", "601.2b");

        if (Named(context) is { } index)
        {
            return index < choose.Options.Count
                ? choose.Options[index]
                : throw new RefusedException("That is not one of the costs offered.", "601.2b");
        }

        foreach (var option in choose.Options)
        {
            try
            {
                _ = Pay(state, context with { Chosen = context.Named.Rest() }, [option]);
            }
            catch (RefusedException)
            {
                continue;
            }

            return option;
        }

        throw new RefusedException("None of the costs offered can be paid.", "601.2h");
    }

    /// <summary>The option the command named, CR 601.2b, or null when it named none.</summary>
    [RuleRef("601.2b")]
    private static int? Named(CostContext context)
    {
        foreach (var choice in context.Choices ?? [])
        {
            if (CastPlan.Names(choice, CostContext.ChooseCostChoice, out var index))
                return index;
        }

        return null;
    }

    /// <summary>
    /// CR 118.8: "pay {X}, where X is that creature's power". The amount is counted when the cost
    /// is announced, CR 601.2h, so the payment is never suspended waiting to learn what it is.
    /// </summary>
    [RuleRef("118.8")]
    private static Outcome PayCounted(GameState state, CostContext context, PayManaCost cost)
    {
        var generic = context.Query.Evaluate(state, cost.Generic, context.Effect);
        if (generic < 0)
            throw new RefusedException("A mana cost cannot be less than nothing.", "118.8");

        ImmutableList<ManaSymbol> symbols = [.. ManaSymbols.Parse(cost.Colored), new ManaSymbol { Generic = generic }];
        return PaySymbols(state, context, symbols, "cost").Outcome;
    }

    /// <summary>
    /// The tap symbol, CR 118.1. An already-tapped permanent cannot pay it, CR 118.4, and a
    /// creature cannot pay it unless it has been under its controller's control since their most
    /// recent turn began, CR 302.6 — the summoning sickness rule, which haste turns off, CR 702.10b.
    /// </summary>
    [RuleRef("302.6")]
    private static Outcome TapTheSource(GameState state, CostContext context)
    {
        var source = Source(context);
        if (source.Tapped)
            throw new RefusedException("It is already tapped.", "118.4");
        if (context.Characteristics is { } chars && chars.Types.HasFlag(CardType.Creature) && source.SummoningSick && !chars.Has(KeywordKind.Haste))
            throw new RefusedException("It has not been under your control since your most recent turn began.", "302.6");

        var tapped = new ObjectChanged(source.Id, Tapped: true);
        return new Outcome(tapped.Apply(state), [tapped]);
    }

    /// <summary>The untap symbol, CR 118.1: only a tapped permanent can pay it.</summary>
    [RuleRef("118.1")]
    private static Outcome UntapTheSource(GameState state, CostContext context)
    {
        var source = Source(context);
        if (!source.Tapped)
            throw new RefusedException("It is not tapped.", "118.4");

        var untapped = new ObjectChanged(source.Id, Tapped: false);
        return new Outcome(untapped.Apply(state), [untapped]);
    }

    /// <summary>Sacrificing the source, CR 701.17: its controller moves it to its owner's graveyard.</summary>
    [RuleRef("701.17")]
    private static Outcome Sacrifice(GameState state, CostContext context)
    {
        var source = Source(context);
        if (source.Zone.Zone != CardZone.Battlefield)
            throw new RefusedException("Only a permanent on the battlefield can be sacrificed.", "701.17a");

        var left = new ObjectLeftBy(source.Id, "sacrificed", null);
        var moved = context.Zones.Move(left.Apply(state), source.Id, ZoneKey.Of(CardZone.Graveyard, source.Owner));
        return new Outcome(moved.State, moved.Events.Insert(0, left));
    }

    /// <summary>
    /// "Discard this card" as a cost, CR 701.9a: from its owner's hand to their graveyard. It is
    /// the cost of cycling (CR 702.29a), so the source is in a hand, not on the battlefield.
    /// </summary>
    [RuleRef("701.9a")]
    private static Outcome Discard(GameState state, CostContext context)
    {
        var source = Source(context);
        if (source.Zone.Zone != CardZone.Hand)
            throw new RefusedException("Only a card in a hand can be discarded.", "701.9a");

        var left = new ObjectLeftBy(source.Id, "discarded", null);
        var moved = context.Zones.Move(left.Apply(state), source.Id, ZoneKey.Of(CardZone.Graveyard, source.Owner));
        return new Outcome(moved.State, moved.Events.Insert(0, left));
    }

    /// <summary>Paying life, CR 119.4: a player can pay only life they have.</summary>
    [RuleRef("119.4")]
    private static Outcome LoseLife(GameState state, PlayerId player, int amount)
    {
        if (amount < 0)
            throw new RefusedException("A life payment cannot be negative.", "119.4");
        if (state.Player(player).Life < amount)
            throw new RefusedException("You cannot pay more life than you have.", "119.4");
        if (amount == 0)
            return Outcome.Unchanged(state);

        var paid = new LifeChanged(player, -amount, "cost");
        return new Outcome(paid.Apply(state), [paid]);
    }

    private static GameObject Source(CostContext context) =>
        context.Source ?? throw new RefusedException("There is no source to pay that cost with.", "601.2h");

    internal static EffectContext Context(ObjectId? source, PlayerId controller, int? x) => new(
        source,
        controller,
        [],
        [],
        x,
        ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
        ImmutableDictionary<string, string>.Empty,
        ImmutableDictionary<string, int>.Empty,
        [],
        null);
}

/// <summary>What paying a mana cost did: the resulting outcome, and the mana that left the pool.</summary>
[RuleRef("601.2h")]
internal sealed record PaidMana(Outcome Outcome, ManaPool Spent);
