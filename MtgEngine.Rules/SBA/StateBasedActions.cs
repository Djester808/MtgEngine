using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Rules.SBA;

/// <summary>
/// Implements State-Based Actions (SBAs) per MTG CR 704.
/// SBAs are checked whenever a player would receive priority.
/// They are applied repeatedly until no more apply.
/// </summary>
public static class StateBasedActions
{
    /// <summary>
    /// Check and apply all SBAs. Returns the new game state and a list of events
    /// that occurred (for triggering abilities). Loops until stable.
    /// </summary>
    public static (GameState State, IReadOnlyList<string> Log) Apply(GameState state)
    {
        var log = new List<string>();
        bool anyApplied;

        do
        {
            anyApplied = false;
            var result = ApplyOnce(state, log);
            if (result.applied)
            {
                state = result.state;
                anyApplied = true;
            }
        }
        while (anyApplied);

        return (state, log);
    }

    private static (GameState state, bool applied) ApplyOnce(GameState state, List<string> log)
    {
        // CR 704.5a - Player with 0 or less life loses
        foreach (var player in state.Players)
        {
            if (player.Life <= 0 && state.Result == GameResult.InProgress)
            {
                log.Add($"{player.Name} lost the game (life total {player.Life}).");
                state = DetermineWinner(state, player.PlayerId);
                return (state, true);
            }
        }

        // CR 704.5c - Player with 10 or more poison counters loses
        foreach (var player in state.Players)
        {
            if (player.PoisonCounters >= 10 && state.Result == GameResult.InProgress)
            {
                log.Add($"{player.Name} lost the game (10 poison counters).");
                state = DetermineWinner(state, player.PlayerId);
                return (state, true);
            }
        }

        // CR 704.5f - Creature with toughness 0 or less is put into graveyard
        foreach (var permanent in state.Battlefield.ToList())
        {
            if (!permanent.IsCreature) continue;
            int? toughness = permanent.EffectiveToughness;
            if (toughness.HasValue && toughness.Value <= 0)
            {
                log.Add($"{permanent.Name} died (toughness {toughness.Value}).");
                state = MovePermanentToGraveyard(state, permanent);
                return (state, true);
            }
        }

        // CR 704.5g - Creature with lethal damage is destroyed
        foreach (var permanent in state.Battlefield.ToList())
        {
            if (!permanent.IsCreature) continue;
            if (permanent.HasKeyword(KeywordAbility.Indestructible)) continue;

            bool hasLethalDamage = permanent.EffectiveToughness.HasValue
                && permanent.DamageMarked >= permanent.EffectiveToughness.Value;

            bool hasDeathtouchDamage = permanent.HasDeathtouchDamage;

            if (hasLethalDamage || hasDeathtouchDamage)
            {
                log.Add($"{permanent.Name} died (lethal damage: {permanent.DamageMarked}).");
                state = MovePermanentToGraveyard(state, permanent);
                return (state, true);
            }
        }

        // CR 704.5j - Legend rule: if two or more legendary permanents with the same name,
        // each player keeps one and puts the rest into graveyard
        var legendaryGroups = state.Battlefield
            .Where(p => p.Definition.Supertypes.Contains("Legendary"))
            .GroupBy(p => (p.Name, p.ControllerId))
            .Where(g => g.Count() > 1);

        foreach (var group in legendaryGroups)
        {
            // Keep the most recently played (last in list), destroy the others
            var toDestroy = group.SkipLast(1).ToList();
            foreach (var legend in toDestroy)
            {
                log.Add($"{legend.Name} legend rule: moved to graveyard.");
                state = MovePermanentToGraveyard(state, legend);
            }
            return (state, true);
        }

        // CR 704.5i - Aura with no legal attachment is put into graveyard
        foreach (var permanent in state.Battlefield.ToList())
        {
            if (!permanent.CardTypes.HasFlag(CardType.Enchantment)) continue;
            if (!permanent.Definition.Subtypes.Contains("Aura")) continue;

            // An aura must be attached to something. If not, goes to graveyard.
            // (Simplified: check that whatever it's attached to still exists)
            // TODO: track attached-to on Permanent in a future iteration
        }

        // CR 704.5q - Planeswalker with 0 loyalty is put into graveyard
        foreach (var permanent in state.Battlefield.ToList())
        {
            if (!permanent.Definition.IsPlaneswalker) continue;
            int loyalty = permanent.Counters.GetValueOrDefault(CounterType.Loyalty);
            if (loyalty <= 0)
            {
                log.Add($"{permanent.Name} left the battlefield (0 loyalty).");
                state = MovePermanentToGraveyard(state, permanent);
                return (state, true);
            }
        }

        return (state, false);
    }

    private static GameState MovePermanentToGraveyard(GameState state, Permanent permanent)
    {
        var owner = state.Players.First(p => p.PlayerId == permanent.SourceCard.OwnerId);
        var updatedOwner = owner.SendToGraveyard(permanent.SourceCard);
        return state
            .RemovePermanent(permanent.PermanentId)
            .UpdatePlayer(updatedOwner)
            with { StateBasedActionsRequired = false };
    }

    private static GameState DetermineWinner(GameState state, Guid losingPlayerId)
    {
        var winner = state.Players.FirstOrDefault(p => p.PlayerId != losingPlayerId);
        var result = winner is null ? GameResult.Draw : GameResult.Player1Wins; // TODO: map winner properly
        return state with { Result = result };
    }
}
