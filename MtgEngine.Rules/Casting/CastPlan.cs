using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Faces;
using MtgEngine.Rules.Keywords;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// Which face is being cast and what it costs, decided before anything is put on the stack:
/// CR 601.3b picks the face, CR 118.9 lets a keyword replace the mana cost, CR 601.2b adds one
/// to it, and the cost-shaping statics of CR 702.40a take generic mana off it. Everything here
/// is read off the printed <see cref="KeywordAbility"/> on the chosen face — no card name, no
/// special case — and anything this slice cannot pay throws a <see cref="RefusedException"/>
/// naming the keyword and its rule rather than casting the spell for the wrong price.
/// </summary>
[RuleRef("601.2b")]
internal sealed record CastPlan(
    CastableFace Chosen,
    Face Printed,
    CardZone From,
    ImmutableList<ManaSymbol> Mana,
    ImmutableList<string> OptionalCostsPaid,
    ImmutableList<Cost> NonMana,
    PlayerId? GiftPromisedTo = null)
{
    /// <summary>The types the spell has on the stack, CR 712.11c: only the chosen face is evaluated.</summary>
    public CardType Types => Printed.Types.Types;

    /// <summary>CR 702.8b: flash on the face being cast widens the window, whichever face that is.</summary>
    public bool HasFlash => Keyword(Printed, KeywordKind.Flash) is not null;

    /// <summary>
    /// CR 601.3b: the player announces which face they are casting before the card moves, and
    /// CR 601.2b–f: the alternative and additional costs are locked in with it, so no payment is
    /// ever suspended.
    /// </summary>
    [RuleRef("601.3b")]
    public static CastPlan For(GameState state, GameObject card, CastSpell command, IQuery query)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(command);
        var definition = card.Definition;
        var zone = card.Zone.Zone;
        var options = FaceRules.CastableFaces(definition, zone, OnAdventure(card));
        if (options.Count == 0)
            throw new RefusedException("A card in that zone cannot be cast.", "601.3");

        var chosen = options.FirstOrDefault(o => o.FaceIndex == command.FaceIndex)
            ?? throw new RefusedException("That face cannot be cast from there.", "601.3b");
        if (chosen.IsLandFace)
        {
            // CR 305.9 for an ordinary land; CR 712.12 for the land face of a modal card, which
            // is played as a land and never cast.
            var normal = definition.Layout == Layout.Normal;
            throw new RefusedException(normal ? "A land is played, not cast." : "A land face is played, not cast.", normal ? "305.9" : "712.12");
        }

        var printed = definition.Faces[chosen.FaceIndex];
        var paid = ImmutableList.CreateBuilder<string>();
        var extra = ImmutableList.CreateBuilder<Cost>();

        // CR 601.2b: an additional cost printed on the spell is announced with it and paid along
        // with the mana cost, whether or not the caster wants to — it is not optional.
        extra.AddRange(printed.AdditionalCosts ?? []);
        var mana = Alternative(card, printed, command, paid, extra) ?? ManaSymbols.Parse(printed.ManaCost);
        mana = Additional(printed, command, paid, extra, mana);
        mana = Shaped(state, printed, query, command.Player, mana);
        return new CastPlan(chosen, printed, zone, mana, paid.ToImmutable(), extra.ToImmutable(), Gift(printed, command));
    }

    /// <summary>
    /// CR 702.174b: the gift is promised to an opponent as the spell is cast, and the promise is
    /// part of the spell — the stack entry keeps it so the spell's own text can ask later. The
    /// opponent promised is the first of the command's <see cref="CastSpell.TargetPlayers"/>: the
    /// gift is not a target (CR 115.10), but the players a cast names is the one channel the
    /// command has for naming one, and a gift promised to nobody is not a promise.
    /// </summary>
    [RuleRef("702.174b")]
    private static PlayerId? Gift(Face printed, CastSpell command)
    {
        if (!(command.OptionalCosts ?? []).Contains(KeywordKind.Gift))
            return null;
        if (Keyword(printed, KeywordKind.Gift) is null)
            throw new RefusedException("That card has no gift to promise.", "702.174b");

        var promised = (command.TargetPlayers ?? []).FirstOrDefault(p => p != command.Player, command.Player);
        return promised == command.Player
            ? throw new RefusedException("A gift is promised to an opponent.", "702.174b")
            : promised;
    }

    /// <summary>CR 715.3d: the card exiled on an adventure is the one card its owner may cast from exile.</summary>
    [RuleRef("715.3d")]
    private static bool OnAdventure(GameObject card) =>
        card.Zone.Zone == CardZone.Exile && card.Definition.Layout == Layout.Adventure;

    /// <summary>
    /// An alternative cost, CR 118.9: the keyword's printed cost is paid instead of the mana
    /// cost, and several of them also say which zone the card is cast from, CR 601.3. Null means
    /// no alternative cost was announced, so the printed mana cost stands.
    /// </summary>
    [RuleRef("118.9")]
    private static ImmutableList<ManaSymbol>? Alternative(GameObject card, Face printed, CastSpell command, ImmutableList<string>.Builder paid, ImmutableList<Cost>.Builder extra)
    {
        var zone = card.Zone.Zone;

        // CR 118.9 and 601.3a: an effect that lets the card be cast without paying its mana cost is
        // both the permission to cast it from where it is and the announcement that no mana cost is
        // paid; every additional cost still is (CR 601.2b), so this is a flag beside the costs.
        if (command.WithoutPayingManaCost)
        {
            paid.Add("free");
            return [];
        }

        if (command.AlternativeCost is not { } kind)
        {
            // CR 601.3: a card is cast from its owner's hand unless an effect says otherwise. The
            // exiled adventurer is the one permission this slice has, CR 715.3d.
            if (zone != CardZone.Hand && !OnAdventure(card))
                throw new RefusedException("A card in that zone cannot be cast without permission.", "601.3");
            return null;
        }

        var semantic = KeywordSemantics.Of(kind);
        if (semantic.Cost != CostShape.Alternative)
            throw new RefusedException($"{kind} is not an alternative cost.", semantic.Rule);
        var ability = Keyword(printed, kind) ?? throw new RefusedException($"That card has no {kind}.", semantic.Rule);
        if (semantic.CastFrom is { } from && from != zone)
            throw new RefusedException($"{kind} is not paid from there.", semantic.Rule);

        // CR 702.34a: flashback's whole rider is exiling the card as it resolves, which
        // Destination does. CR 702.138a: escape's is the non-mana half printed beside its mana,
        // which KeywordAbility.Costs carries and CostPayment pays. The rest name themselves and
        // their rule rather than casting a spell whose rider the engine would drop.
        if (kind is not (KeywordKind.Flashback or KeywordKind.Escape))
            throw new RefusedException($"{kind} cannot be paid yet.", semantic.Rule);
        if (!ManaSymbols.TryParse(ability.Cost, out var symbols, out _))
            throw new RefusedException($"The {kind} cost cannot be paid yet.", "601.2h");

        extra.AddRange(ability.Costs ?? []);
        paid.Add(kind.ToString());
        return symbols;
    }

    /// <summary>
    /// The additional costs a player chose to pay, CR 601.2b: each is paid on top of the mana
    /// cost and recorded, so "if it was kicked" (CR 702.32c) can be asked when the spell resolves.
    /// </summary>
    [RuleRef("601.2b")]
    private static ImmutableList<ManaSymbol> Additional(Face printed, CastSpell command, ImmutableList<string>.Builder paid, ImmutableList<Cost>.Builder extra, ImmutableList<ManaSymbol> mana)
    {
        var times = new Dictionary<KeywordKind, int>();
        foreach (var kind in command.OptionalCosts ?? [])
        {
            // CR 702.174b: promising a gift costs nothing and is not an additional cost, so it is
            // recorded (Gift) and skipped here rather than priced.
            if (kind == KeywordKind.Gift)
            {
                paid.Add(kind.ToString());
                continue;
            }

            var semantic = KeywordSemantics.Of(kind);
            if (semantic.Cost != CostShape.Additional)
                throw new RefusedException($"{kind} is not an additional cost.", semantic.Rule);
            var ability = Keyword(printed, kind) ?? throw new RefusedException($"That card has no {kind}.", semantic.Rule);

            // CR 702.32b: multikicker may be paid any number of times, and KeywordAbility.Repeatable
            // is how the printed text says so. An ordinary kicker is paid once, CR 702.32a.
            times[kind] = times.GetValueOrDefault(kind) + 1;
            if (times[kind] > 1 && !ability.Repeatable)
                throw new RefusedException($"A {kind} cost cannot be paid more than once.", semantic.Rule);
            if (!ManaSymbols.TryParse(ability.Cost, out var more, out _))
                throw new RefusedException($"The {kind} cost cannot be paid yet.", "601.2h");

            // CR 601.2b: each payment is a separate additional cost, so a cost paid twice is paid
            // twice over and recorded twice — "if it was kicked twice" counts the entries.
            mana = mana.AddRange(more);
            extra.AddRange(ability.Costs ?? []);
            paid.Add(kind.ToString());
        }

        return mana;
    }

    /// <summary>
    /// The statics that shape the cost while the spell is on the stack. Affinity is not optional,
    /// CR 702.40a, so it is applied here and counted from the battlefield. Convoke, improvise and
    /// delve (CR 702.51a, 702.126a, 702.66a) are payments a player <em>may</em> make, and there is
    /// no channel on the command for the permanents or cards they would use, so a spell with one
    /// of them is cast for its full price — which is a legal choice, not a dropped rule.
    /// </summary>
    [RuleRef("702.40a")]
    private static ImmutableList<ManaSymbol> Shaped(GameState state, Face printed, IQuery query, PlayerId player, ImmutableList<ManaSymbol> mana)
    {
        var reduction = 0;
        foreach (var keyword in printed.AbilityList.OfType<KeywordAbility>().Where(k => k.Keyword == KeywordKind.Affinity))
            reduction += Affinity(state, keyword, query, player);
        return reduction == 0 ? mana : ReduceGeneric(mana, reduction);
    }

    /// <summary>
    /// CR 702.40a: "affinity for [text]" costs {1} less to cast for each matching permanent its
    /// controller controls. The filter comes off the printed keyword; the "you control" half is
    /// the rule's, so only the caster's permanents are counted whatever the filter says.
    /// </summary>
    [RuleRef("702.40a")]
    private static int Affinity(GameState state, KeywordAbility keyword, IQuery query, PlayerId player)
    {
        var filter = keyword.Of ?? throw new RefusedException("An affinity with nothing to count cannot be applied.", "702.40a");
        var context = CostPayment.Context(null, player, null);
        var count = 0;
        foreach (var obj in state.Objects.Values)
        {
            if (obj.Zone.Zone != CardZone.Battlefield || obj.InitialController != player)
                continue;
            if (query.Matches(state, filter, obj.Id, context))
                count++;
        }

        return count;
    }

    /// <summary>
    /// CR 601.2f and 118.7: a cost reduction comes off the generic part of the cost and can never
    /// take it below nothing, so the coloured symbols are still paid in full.
    /// </summary>
    [RuleRef("601.2f")]
    private static ImmutableList<ManaSymbol> ReduceGeneric(ImmutableList<ManaSymbol> mana, int reduction)
    {
        var result = mana;
        for (var i = 0; i < result.Count && reduction > 0; i++)
        {
            var symbol = result[i];
            if (symbol.IsX || symbol.IsSnow || !symbol.IsAnyType || symbol.Generic <= 0)
                continue;
            var taken = Math.Min(symbol.Generic, reduction);
            reduction -= taken;
            result = result.SetItem(i, symbol with { Generic = symbol.Generic - taken });
        }

        return result;
    }

    private static KeywordAbility? Keyword(Face printed, KeywordKind kind) =>
        printed.AbilityList.OfType<KeywordAbility>().FirstOrDefault(k => k.Keyword == kind);
}
