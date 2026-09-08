using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Query;

/// <summary>
/// Wave 28's amounts: what a firing carried about somebody else's prevention (CR 615.13) and about
/// the payment for the spell it watched (CR 400.7d), and the four V16 numbers — the greatest damage
/// one player took this turn (CR 102.2), the creatures that died (CR 700.4), the prevention this
/// effect is itself running (CR 615) and the largest group of permanents sharing a name (CR 201.2).
/// <para>
/// Every one of them is a number no other number stands in for. Three of the six refuse rather than
/// answer zero, and each refusal is the same shape: a key nobody wrote means the question was never
/// asked, and zero is a legal answer to it, so answering zero would play the card and look right.
/// </para>
/// </summary>
public sealed partial class Query
{
    /// <summary>
    /// The key the damage <em>this</em> effect's own prevention prevented is carried under, CR 615.
    /// Nothing writes it yet: the replacement road computes the number in
    /// <c>ReplacementSystem.Spend</c> — <c>min(shield, damage)</c> — and drops it, and the context it
    /// hands the interpreter for the "instead" walk carries an empty <c>Chosen</c>. The constant is
    /// spelled here so the reader and the future writer cannot drift, exactly as
    /// <see cref="TriggeringLifeGainedAmount"/> was spelled here before the trigger system owned it;
    /// seam-requests/E5l.md § 1 asks the replacement road for the write.
    /// </summary>
    [RuleRef("615")]
    public const string PreventedDamageAmount = "prevented-damage";

    /// <summary>
    /// "for each 1 damage prevented this way", CR 615: the damage the prevention this ability is
    /// running actually prevented — 2 for a shield that meets 2 of 5, 0 for one that meets nothing.
    /// <para>
    /// A missing key is refused and never read as 0. Zero is a real answer here — a shield that met
    /// no damage prevented none — so a reader that answered 0 for "no prevention has run" would be
    /// indistinguishable from the honest zero, and the card would resolve dealing nothing while the
    /// log said it played. The linter already guarantees the ability holds a <c>PreventDamage</c>
    /// (L045), so the only way the key is absent is that the road has not written it.
    /// </para>
    /// </summary>
    [RuleRef("615")]
    private static int PreventedDamageOf(EffectContext ctx) =>
        ctx.Chosen.ContainsKey(PreventedDamageAmount)
            ? CarriedNumber(ctx, PreventedDamageAmount)
            : throw Unanswerable(
                nameof(Cards.PreventedDamage),
                "no prevention in this effect has said how much it prevented; the replacement road carries the amount (seam-requests/E5l.md)",
                "615");

    /// <summary>
    /// What was spent to cast the spell that fired this ability, CR 400.7d: the firing carries the
    /// total under <see cref="TriggerSystem.ManaSpentOnTriggeringAmount"/> and one key per colour
    /// actually paid under <see cref="TriggerSystem.ManaSpentOnTriggeringColorPrefix"/>.
    /// <para>
    /// Two readings this must not have. It never reads <c>StackEntry.ManaSpent</c> off the source —
    /// that is <see cref="ManaSpentOnThis"/>, a different spell, and the triggering spell has
    /// usually resolved and ceased to exist (CR 400.7) by the time this ability does. And an absent
    /// total is refused, not answered 0: "if no mana was spent to cast it" is the total present and
    /// equal to zero, while no total at all is a firing that watched no cast, and the two must not
    /// look alike. A colour nobody paid has no key of its own and is the honest 0 — the total's
    /// presence is what proves a cast was watched.
    /// </para>
    /// </summary>
    [RuleRef("400.7d")]
    private static int ManaSpentOnTriggeringSpell(Color? ofColor, EffectContext ctx)
    {
        if (!ctx.Chosen.ContainsKey(TriggerSystem.ManaSpentOnTriggeringAmount))
        {
            throw Unanswerable(
                nameof(ManaSpentOnTriggering),
                "this ability did not fire on a spell being cast, so there is no payment to read",
                "400.7d");
        }

        return ofColor is { } color
            ? CarriedNumber(ctx, TriggerSystem.ManaSpentOnTriggeringColorPrefix + color)
            : CarriedNumber(ctx, TriggerSystem.ManaSpentOnTriggeringAmount);
    }

    /// <summary>
    /// "if an opponent was dealt 7 or more damage this turn", CR 102.2 with CR 120.3: the greatest
    /// damage dealt this turn to any one player the filter admits. The printed sentence is
    /// existential, and the maximum answers it exactly — the greatest is at least seven precisely
    /// when one of them was dealt at least seven.
    /// <para>
    /// It is never a sum. A table that spread seven damage over three opponents totals seven and
    /// contains no such opponent; in a duel the two folds agree, so a two-player test cannot tell
    /// them apart and the wrong reading would survive one. With <c>By</c> named the pair map answers
    /// per player, because "damage those sources dealt to that player" is one question about two
    /// halves and neither total can answer it alone. No admitted player is zero rather than a
    /// refusal: nobody was dealt anything, which is what the number then stands for.
    /// </para>
    /// </summary>
    [RuleRef("102.2")]
    private int GreatestDamageTo(GameState state, GreatestDamageDealtThisTurn greatest, EffectContext ctx)
    {
        // CR 800.4a: a player who has left the game is no longer a player of it, so their damage is
        // not "an opponent's" any more — the same seats EachPlayer resolves to.
        var seats = state.Players
            .Where(p => !p.HasLost && PlayerMatches(state, greatest.To, p.Id, ctx))
            .Select(p => p.Id)
            .ToList();
        if (seats.Count == 0)
            return 0;

        if (greatest.By is not { } by)
            return seats.Max(p => state.History.DamageReceivedByPlayer.GetValueOrDefault(p));

        var sources = Resolve(state, by, ctx).ToHashSet();
        var pairs = state.History.DamageDealtToPlayer;
        return seats.Max(seat => pairs
            .Where(pair => pair.Key.To == seat && sources.Contains(pair.Key.Source))
            .Sum(pair => pair.Value));
    }

    /// <summary>
    /// "the number of creatures that died this turn", CR 700.4: died means put into a graveyard from
    /// the battlefield, so this counts the moves the turn recorded and never the cards lying in a
    /// graveyard now. A creature that died and was then exiled, returned or shuffled away still
    /// counts; a creature card milled or discarded never died and never does.
    /// <para>
    /// The departed object is read as it last existed (CR 603.10, the same look-back
    /// <see cref="CreatureDied"/> and <see cref="WasAsCreated"/> use), so the filter is asked of its
    /// printed characteristics. Two shapes are refused rather than guessed: a filter naming a zone
    /// asks where a card that already left the battlefield is, which is not what "died" counts, and
    /// a filter with an <c>Also</c> predicate would ask a board question about an object the state no
    /// longer holds.
    /// </para>
    /// </summary>
    [RuleRef("700.4")]
    private int CreaturesDied(GameState state, CreaturesDiedThisTurn died, EffectContext ctx)
    {
        if (died.Matching is not { } filter)
            return state.History.Died.Count(WasCreature);

        if (filter.In is not null)
        {
            throw Unanswerable(
                nameof(CreaturesDiedThisTurn),
                "a death is the move a creature made and not where its card is now, so the filter cannot name a zone",
                "700.4");
        }

        if (filter.Also is not null)
        {
            throw Unanswerable(
                nameof(CreaturesDiedThisTurn),
                "a look-back filter cannot ask a predicate of an object the state no longer holds",
                "700.4");
        }

        return state.History.Died.Count(o => WasCreature(o) && MatchesAs(state, filter, o, _layers.PrintedOf(o), ctx));
    }

    /// <summary>
    /// "if you control three or more lands with the same name", CR 201.2 with 201.2a: the size of the
    /// largest group of admitted objects sharing one name. A plain <see cref="CountOf"/> is satisfied
    /// by three differently named lands, which is the card played wrongly with nothing in the log to
    /// say so.
    /// <para>
    /// CR 201.2a: an object with no name shares its name with nothing, <em>including</em> another
    /// nameless object, so nameless permanents never group — each is a group of one. A filter that
    /// already names the name is a different sentence (a count) and is refused rather than answered,
    /// because grouping by one fixed name would make every such card read as a plain count.
    /// </para>
    /// </summary>
    [RuleRef("201.2")]
    private int GreatestSameName(GameState state, GreatestSameNameCount group, EffectContext ctx)
    {
        if (group.Filter.Named is { } named)
        {
            throw Unanswerable(
                nameof(GreatestSameNameCount),
                $"the group shares a name the card does not print, but the filter already names \"{named}\"",
                "201.2");
        }

        var names = Candidates(state, group.Filter, ctx).Select(id => _layers.Of(state, id).Name).ToList();
        if (names.Count == 0)
            return 0;

        // CR 201.2a: a nameless object is its own group, so it contributes one and never joins another.
        var nameless = names.Any(string.IsNullOrEmpty) ? 1 : 0;
        var largest = names
            .Where(name => !string.IsNullOrEmpty(name))
            .GroupBy(name => name, StringComparer.Ordinal)
            .Select(sharing => sharing.Count())
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(largest, nameless);
    }
}
