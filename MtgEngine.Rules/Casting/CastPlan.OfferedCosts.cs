using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// The two costs a spell is <em>offered</em> as it is announced rather than told to pay: the
/// alternative costs printed on the face (CR 118.9, <see cref="Face.AlternativeCosts"/>), one of
/// which replaces the mana cost, and the additional costs a lasting effect offers on other players'
/// spells (CR 118.8 with 601.2f, <see cref="OptionalAdditionalCost"/>), each of which is paid on top
/// and may buy the caster a cost reduction for that one casting.
/// <para>
/// Both are announced on the command's <c>CostChoices</c>, the list every other cost choice already
/// rides (CR 601.2h) — <c>"alt"</c> or <c>"alt:2"</c> for a printed alternative, <c>"addcost"</c> or
/// <c>"addcost:1"</c> for an offer — so no payment is ever suspended to ask for one and nothing new
/// has to reach the command seam. A caster who says neither pays as printed, which is a legal choice.
/// </para>
/// </summary>
[RuleRef("118.9")]
internal sealed partial record CastPlan
{
    /// <summary>The word on <c>CostChoices</c> that takes a printed alternative cost, CR 118.9.</summary>
    internal const string AlternativeCostChoice = "alt";

    /// <summary>The word on <c>CostChoices</c> that takes an offered additional cost, CR 118.8.</summary>
    internal const string AdditionalCostChoice = "addcost";

    /// <summary>
    /// How an offered additional cost the caster paid is recorded on
    /// <see cref="StackEntry.OptionalCostsPaid"/>, so "if you paid it this way" can be asked of the
    /// spell later (CR 118.8). The reduction the payment bought is applied here, at announcement,
    /// and not from the record.
    /// </summary>
    [RuleRef("118.8")]
    internal const string PaidOfferedAdditionalCost = "additional";

    /// <summary>
    /// The printed alternative cost this announcement takes, CR 118.9: the one the command names on
    /// <c>CostChoices</c>, whose <see cref="AlternativeCost.OnlyIf"/> is read once here in the
    /// caster's context. Null when the command names none, which is every cast that pays as printed.
    /// </summary>
    /// <exception cref="RefusedException">
    /// CR 118.9 when the face prints no such alternative or its condition does not hold, CR 118.9c
    /// when two are named — a spell is cast for one alternative cost.
    /// </exception>
    [RuleRef("118.9")]
    private static AlternativeCost? PrintedAlternative(GameState state, GameObject card, Face printed, CastSpell command, IQuery query)
    {
        var offered = printed.AlternativeCosts ?? [];
        AlternativeCost? chosen = null;
        foreach (var choice in command.CostChoices ?? [])
        {
            if (!Names(choice, AlternativeCostChoice, out var index))
                continue;
            if (index >= offered.Count)
                throw new RefusedException("That card prints no such alternative cost.", "118.9");
            if (chosen is not null)
                throw new RefusedException("A spell is cast for only one alternative cost.", "118.9c");

            var alternative = offered[index];

            // CR 118.9: the condition on the printed alternative is read as the spell is announced
            // and never again — the cast is for that price whatever the game does afterwards.
            if (alternative.OnlyIf is { } test && !query.Holds(state, test, CostPayment.Context(card.Id, command.Player, command.X)))
                throw new RefusedException("That alternative cost cannot be paid now.", "118.9");
            chosen = alternative;
        }

        return chosen;
    }

    /// <summary>
    /// The optional additional costs a lasting effect offers this cast, CR 118.8 with 601.2f. The
    /// active effects are read the way <see cref="Permission"/> reads a lasting permission — the
    /// effect's own <see cref="OptionalAdditionalCost.Spells"/> filter and
    /// <see cref="OptionalAdditionalCost.Who"/>, or the objects and players a resolution named on the
    /// <see cref="ActiveEffect"/> — so a cost offer and a play permission can never disagree about
    /// which spells and players an effect reaches.
    /// <para>
    /// The caster may decline every offer, and declining changes nothing else about the spell. A cost
    /// the caster took is paid with the other non-mana costs (CR 601.2f) and what it bought
    /// (<see cref="OptionalAdditionalCost.IfPaid"/>) applies to this casting only, which is why it is
    /// applied to the cost here rather than left on the board as a static effect.
    /// </para>
    /// </summary>
    [RuleRef("118.8")]
    private static ImmutableList<ManaSymbol> Offered(
        GameState state,
        GameObject card,
        CastSpell command,
        IQuery query,
        ImmutableList<string>.Builder paid,
        ImmutableList<Cost>.Builder extra,
        ImmutableList<ManaSymbol> mana)
    {
        var taken = ImmutableList.CreateBuilder<int>();
        foreach (var choice in command.CostChoices ?? [])
        {
            if (!Names(choice, AdditionalCostChoice, out var index))
                continue;
            if (taken.Contains(index))
                throw new RefusedException("That additional cost is offered once.", "601.2b");
            taken.Add(index);
        }

        if (taken.Count == 0)
            return mana;

        var offers = Offers(state, card, command, query);
        var context = CostPayment.Context(card.Id, command.Player, command.X);
        foreach (var index in taken)
        {
            if (index >= offers.Count)
                throw new RefusedException("Nothing offers that additional cost.", "118.8");
            var offer = offers[index];
            extra.Add(offer.Cost);
            paid.Add(PaidOfferedAdditionalCost);
            mana = Bought(state, query, context, offer.IfPaid, mana);
        }

        return mana;
    }

    /// <summary>
    /// Every offer in force for this cast, in the order the state keeps its active effects, which is
    /// the order the command's indices name. CR 118.8: an offer reaches a spell when the effect's
    /// filter admits it and its player half admits the caster.
    /// </summary>
    [RuleRef("118.8")]
    private static ImmutableList<OptionalAdditionalCost> Offers(GameState state, GameObject card, CastSpell command, IQuery query)
    {
        var found = ImmutableList.CreateBuilder<OptionalAdditionalCost>();
        var context = CostPayment.Context(null, command.Player, null);
        foreach (var active in state.ActiveEffects)
        {
            if (active.Effect is not OptionalAdditionalCost offer)
                continue;
            var names = active.AppliesTo.Contains(card.Id)
                || (active.AppliesTo.IsEmpty && query.Matches(state, offer.Spells, card.Id, context));
            var whose = active.AppliesToPlayers.Contains(command.Player)
                || (active.AppliesToPlayers.IsEmpty && query.Players(state, offer.Who, context).Contains(command.Player));
            if (names && whose)
                found.Add(offer);
        }

        return found.ToImmutable();
    }

    /// <summary>
    /// What paying an offered additional cost bought, CR 601.2f: a <see cref="CostModifier"/> whose
    /// <see cref="CostModifier.Delta"/> comes off the cost as it stands for this casting only. A
    /// negative delta is the reduction the corpus spells, a positive one is "costs that much more",
    /// and <see cref="CostModifier.OnlyColor"/> narrows a reduction to symbols of that colour so a
    /// spell with none left in its cost is not reduced further (CR 118.7 keeps a cost at nothing).
    /// Anything else a card buys is refused naming CR 118.8 rather than paid for and dropped.
    /// </summary>
    [RuleRef("601.2f")]
    private static ImmutableList<ManaSymbol> Bought(GameState state, IQuery query, EffectContext context, ContinuousEffect? bought, ImmutableList<ManaSymbol> mana)
    {
        switch (bought)
        {
            case null:
                return mana;

            case CostModifier modifier:
                var delta = query.Evaluate(state, modifier.Delta, context);
                if (delta > 0)
                    return mana.Add(new ManaSymbol { Generic = delta });
                if (delta == 0)
                    return mana;
                if (modifier.OnlyColor is { } color)
                    return ReduceColored(mana, -delta, color);
                if (modifier.GenericOnly)
                    return ReduceGeneric(mana, -delta);
                throw new RefusedException("A reduction that names neither the generic part nor a colour cannot be applied yet.", "601.2f");

            default:
                throw new RefusedException($"Paying that cost cannot buy a {bought.GetType().Name} yet.", "118.8");
        }
    }

    /// <summary>
    /// CR 601.2f with 118.7: a reduction tied to one colour takes that colour's symbols off the cost
    /// and nothing else, and can never take the cost below nothing — a hybrid or Phyrexian symbol the
    /// colour only half pays for is left alone, because reducing it would change which mana may pay
    /// the rest of it (CR 107.4e, 107.4f).
    /// </summary>
    [RuleRef("601.2f")]
    private static ImmutableList<ManaSymbol> ReduceColored(ImmutableList<ManaSymbol> mana, int reduction, Color color)
    {
        var result = mana;
        for (var i = result.Count - 1; i >= 0 && reduction > 0; i--)
        {
            var symbol = result[i];
            if (symbol.IsX || symbol.IsSnow || symbol.IsPhyrexian || symbol.HybridGeneric > 0 || symbol.AllowsColorless)
                continue;
            if (symbol.Generic > 0 || symbol.Colors != color)
                continue;
            reduction--;
            result = result.RemoveAt(i);
        }

        return result;
    }

    /// <summary>
    /// The <c>"alt"</c>/<c>"alt:2"</c> shape a cost choice names an offer with: the bare word is the
    /// first one, and a number after a colon is the index. A choice that is neither is another
    /// reader's (CR 601.2h) and is passed over; a number that is not one is refused as no offer.
    /// </summary>
    private static bool Names(string? choice, string word, out int index)
    {
        index = 0;
        if (string.IsNullOrEmpty(choice))
            return false;
        if (string.Equals(choice, word, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!choice.StartsWith(word + ":", StringComparison.OrdinalIgnoreCase))
            return false;

        var after = choice[(word.Length + 1)..];
        if (!int.TryParse(after, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0)
            throw new RefusedException("That is not one of the costs offered.", "601.2h");
        return true;
    }
}
