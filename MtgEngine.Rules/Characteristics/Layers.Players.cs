using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Layers;

/// <summary>
/// The player-scoped half of the layer system, CR 613.10: a continuous effect that applies to a
/// player rather than to an object is not applied in the layers at all — it is read off the state
/// whenever a rule asks whether that player may do, or have done to them, the thing it forbids.
/// <para>
/// Two sources, one answer. A resolving spell or ability left an <see cref="ActiveEffect"/> whose
/// <see cref="ActiveEffect.AppliesToPlayers"/> named its players when it began (CR 611.2c); a
/// permanent's static ability names its players every time it is asked (CR 611.3a), through
/// <see cref="StaticAbility.Players"/>.
/// </para>
/// </summary>
public sealed partial class LayerSystem
{
    /// <inheritdoc/>
    [RuleRef("613.10")]
    public PlayerLimits PlayerRestrictions(GameState state, PlayerId player)
    {
        ArgumentNullException.ThrowIfNull(state);

        var limits = PlayerLimits.None;

        // CR 611.2c: the set of players was locked in when the effect began, so it is read as written.
        foreach (var active in state.ActiveEffects.Where(e => e.AppliesToPlayers.Contains(player)))
            limits = Restrict(limits, active.Effect);

        // Timestamps do not order this: a union of prohibitions commutes, and nothing here can
        // turn a "can't" back off (CR 101.2), so there is no later effect to win.
        foreach (var effect in PlayerStatics(state, player))
            limits = Restrict(limits, effect);

        return limits;
    }

    /// <summary>
    /// Every static ability on the battlefield whose continuous effect applies to this player,
    /// CR 604.2 and 611.3a. The object-scoped gather in <c>Statics</c> skips these, because it
    /// reads <see cref="StaticAbility.To"/> and a player-scoped ability has none.
    /// </summary>
    [RuleRef("604.2")]
    private IEnumerable<ContinuousEffect> PlayerStatics(GameState state, PlayerId player)
    {
        foreach (var source in state.Objects.Values.Where(o => o.Zone.Zone == Zone.Battlefield))
        {
            // CR 708.2a: a face-down permanent has no text, so it has no static abilities.
            if (source.FaceDown)
                continue;

            var controller = ControllerFrom(source, FromResolutions(state, source.Id));
            foreach (var ability in source.Face.AbilityList.OfType<StaticAbility>())
            {
                // CR 611.3b: the effect applies while its source is in the zone the ability works in.
                if (ability.Where != Zone.Battlefield)
                    continue;

                // A conditional static ("as long as you control an artifact") needs IQuery.Holds,
                // which the layer system still does not have; see seam-requests/E7.md.
                if (ability.While is not null)
                    continue;

                if (Scope(ability) is { } who && IsPlayer(state, who, player, controller))
                    yield return ability.Effect;
            }
        }
    }

    /// <summary>
    /// Which players a static ability's effect applies to, or null when it applies to objects.
    /// <see cref="StaticAbility.Players"/> is the spelling; the three older records carry their own
    /// <c>Who</c> from before that field existed, and a card file that spells them the old way is
    /// read the same as one that spells them the new way.
    /// </summary>
    [RuleRef("613.10")]
    private static PlayerRef? Scope(StaticAbility ability) => ability.Players ?? ability.Effect switch
    {
        CantLoseGame cant => cant.Who,
        CantWinGame cant => cant.Who,
        CantGainLife cant => cant.Who,
        _ => null,
    };

    /// <summary>
    /// One player-scoped effect folded into the limits. The three older records map onto the same
    /// answer as the <see cref="PlayerCant"/> flags they overlap, so a reader asks one question
    /// and never has to know which spelling a card file used.
    /// </summary>
    [RuleRef("101.2")]
    private static PlayerLimits Restrict(PlayerLimits limits, ContinuousEffect effect) => effect switch
    {
        PlayerCant cant => limits with { Cant = limits.Cant | cant.What },

        // CR 104.3 read as a prohibition: the player half of "you can't lose the game".
        CantLoseGame => limits with { Cant = limits.Cant | PlayerRestriction.Lose },

        // CR 104.2b: an effect that would state this player wins does nothing. CR 104.2a is not
        // one of those — it overrides "can't win" explicitly — so this flag never ends a game.
        CantWinGame => limits with { CantWin = true },

        // CR 119.3: no life-gain event reaches a player who can't gain life.
        CantGainLife => limits with { CantGainLife = true },

        // CR 702.16j: can't be targeted, can't be enchanted, all damage to them is prevented.
        PlayerProtectionFromEverything => limits with { ProtectionFromEverything = true },

        _ => limits,
    };
}
