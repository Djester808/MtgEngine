using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// CR 601.3's prohibition half. Everything else in this folder answers "may this player cast this
/// card from here" by finding the permission that allows it; this file is the other side of the
/// same sentence — an effect that says a player <em>can't</em> cast, which no permission overrides.
/// </summary>
public sealed partial class CastingSystem
{
    /// <summary>
    /// CR 601.3: "a player can begin to cast a spell only if a rule or effect allows that player to
    /// cast it and no rule or effect prohibits that player from casting it." The prohibition is
    /// <see cref="CantCastSpells"/> on the state, and it is asked on both roads into
    /// <c>CastCore</c>: an instruction to cast (CR 608.2g) is an effect allowing the cast, and this
    /// rule is a conjunction, so allowing it does not answer the prohibition.
    /// <para>
    /// <see cref="CantCastSpells.CastFrom"/> is a list because "players can't cast spells from
    /// graveyards or libraries" is <em>one</em> restriction over two zones (L047): two static
    /// abilities in its place would have independent durations and the card prints one. No zones
    /// at all means every zone, which is what the record says when the list is absent or empty —
    /// writing the whole zone list out would be a second spelling of the same restriction.
    /// </para>
    /// <para>
    /// The filter is asked about the card where the card is now, the way
    /// <see cref="MayCastAsThoughItHadFlash"/> asks its own: the check runs before CR 601.2a moves
    /// anything, so there is no spell on the stack yet, and a filter naming no zone would otherwise
    /// be read against the battlefield and match nothing.
    /// </para>
    /// </summary>
    [RuleRef("601.3")]
    private void RequireNoCastProhibition(GameState state, PlayerId player, GameObject card)
    {
        var context = CostPayment.Context(null, player, null);
        foreach (var active in state.ActiveEffects)
        {
            if (active.Effect is not CantCastSpells restriction)
                continue;

            var whose = active.AppliesToPlayers.Contains(player)
                || (active.AppliesToPlayers.IsEmpty && query.Players(state, restriction.Who, context).Contains(player));
            if (!whose)
                continue;

            // CR 601.3 with 400.1: an empty or absent zone list is every zone a card can be cast
            // from, so a restriction that names none prohibits the cast wherever the card is.
            if (restriction.CastFrom is { Count: > 0 } zones && !zones.Contains(card.Zone.Zone))
                continue;
            if (restriction.Matching is { } what
                && !query.Matches(state, what with { In = card.Zone.Zone }, card.Id, context))
            {
                continue;
            }

            throw new RefusedException("An effect prohibits casting that spell.", "601.3");
        }
    }
}
