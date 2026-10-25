using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Casting;

/// <summary>One permission reader for land plays and spell announcements.</summary>
internal static class ZonePlayPermissions
{
    internal sealed record Permission(MayPlayFromZone Effect, int? Id = null, StaticPlayPermission? Static = null);

    internal static Permission? Find(GameState state, GameObject card, PlayerId player,
        IQuery query, ICharacteristics characteristics)
    {
        foreach (var active in state.ActiveEffects)
        {
            if (active.Effect is not MayPlayFromZone effect || !InZone(state, card, effect))
                continue;
            var context = CostPayment.Context(active.Source, active.Controller, null);
            var names = active.AppliesTo.Contains(card.Id)
                || (active.AppliesTo.IsEmpty && (effect.What is null || query.Matches(state, effect.What, card.Id, context)));
            var whose = active.AppliesToPlayers.Contains(player)
                || (active.AppliesToPlayers.IsEmpty && query.Players(state, effect.Who, context).Contains(player));
            var permission = new Permission(effect, active.Id);
            if (names && whose && !Spent(state, permission, player))
                return permission;
        }

        foreach (var source in state.Objects.Values.OrderBy(source => source.Id.Value))
        {
            if (source.FaceDown || source.PhasedOut)
                continue;
            var appearance = characteristics.Of(state, source.Id);
            var context = CostPayment.Context(source.Id, appearance.Controller, null);
            for (var index = 0; index < appearance.Abilities.Count; index++)
            {
                if (appearance.Abilities[index] is not StaticAbility { Effect: MayPlayFromZone effect } ability
                    || ability.Where != source.Zone.Zone || !InZone(state, card, effect)
                    || !query.Players(state, ability.Players ?? effect.Who, context).Contains(player)
                    || ability.To is { } selector && !query.Resolve(state, selector, context).Contains(card.Id)
                    || ability.While is { } condition && !query.Holds(state, condition, context)
                    || effect.What is { } filter && !query.Matches(state, filter, card.Id, context))
                    continue;
                var origin = appearance.AbilityOrigins is { } origins && index < origins.Count ? origins[index] : null;
                var identity = new StaticPlayPermission(source.Id, origin?.Slot ?? index,
                    origin is { Category: "printed", Face: 0 } ? null : origin);
                var permission = new Permission(effect, Static: identity);
                if (!Spent(state, permission, player))
                    return permission;
            }
        }
        return null;
    }

    private static bool InZone(GameState state, GameObject card, MayPlayFromZone effect) =>
        card.Zone.Zone == effect.From && (!effect.TopOfLibraryOnly
            || card.Zone.Zone == Zone.Library && state.In(card.Zone).FirstOrDefault() == card.Id);

    private static bool Spent(GameState state, Permission permission, PlayerId player) =>
        permission.Effect.OncePerTurn && (
            state.History.SpellsCast.Any(record => record.By == player
                && record.Permission == permission.Id && record.StaticPermission == permission.Static)
            || state.History.LandsPlayed.Any(record => record.By == player
                && record.Permission == permission.Id && record.StaticPermission == permission.Static));
}
