using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Rules;

/// <summary>
/// Handles all zone transitions: casting spells, playing lands, permanents entering/leaving
/// the battlefield, cards going to graveyard or exile, etc.
/// </summary>
public static class ZoneManager
{
    // =========================================================
    // Playing lands
    // =========================================================

    public static GameState PlayLand(GameState state, Guid playerId, Guid cardId)
    {
        var player = state.GetPlayer(playerId);

        if (player.HasLandPlayedThisTurn)
            throw new InvalidOperationException("You may only play one land per turn.");

        if (state.PriorityPlayerId != playerId)
            throw new InvalidOperationException("You do not have priority.");

        if (state.CurrentPhase != Phase.PreCombatMain && state.CurrentPhase != Phase.PostCombatMain)
            throw new InvalidOperationException("You may only play a land during your main phase.");

        if (!state.IsStackEmpty)
            throw new InvalidOperationException("You cannot play a land while the stack is non-empty.");

        var card = player.Hand.FirstOrDefault(c => c.CardId == cardId)
            ?? throw new InvalidOperationException($"Card {cardId} not in hand.");

        if (!card.IsLand)
            throw new InvalidOperationException($"{card.Name} is not a land.");

        var updatedPlayer = player
            .RemoveCardFromHand(cardId) with { HasLandPlayedThisTurn = true };

        var permanent = CreatePermanent(card, playerId);
        return state
            .UpdatePlayer(updatedPlayer)
            .AddPermanent(permanent);
    }

    // =========================================================
    // Tapping for mana
    // =========================================================

    public static GameState TapLandForMana(GameState state, Guid playerId, Guid permanentId)
    {
        var permanent = state.GetPermanent(permanentId);

        if (permanent.ControllerId != playerId)
            throw new InvalidOperationException("You do not control that permanent.");

        if (permanent.IsTapped)
            throw new InvalidOperationException($"{permanent.Name} is already tapped.");

        if (!permanent.IsLand)
            throw new InvalidOperationException($"{permanent.Name} is not a land.");

        var color = permanent.Definition.BasicLandColor
            ?? throw new InvalidOperationException($"{permanent.Name} does not have a basic land mana ability.");

        var updatedPermanent = permanent.Tap();
        var updatedPlayer = state.GetPlayer(playerId).AddMana(color);

        return state
            .UpdatePermanent(updatedPermanent)
            .UpdatePlayer(updatedPlayer);
    }

    // =========================================================
    // Untapping lands (undo mana activation)
    // =========================================================

    public static GameState UntapLand(GameState state, Guid playerId, Guid permanentId)
    {
        var permanent = state.GetPermanent(permanentId);

        if (permanent.ControllerId != playerId)
            throw new InvalidOperationException("You do not control that permanent.");

        if (!permanent.IsTapped)
            throw new InvalidOperationException($"{permanent.Name} is not tapped.");

        if (!permanent.IsLand)
            throw new InvalidOperationException($"{permanent.Name} is not a land.");

        var color = permanent.Definition.BasicLandColor
            ?? throw new InvalidOperationException($"{permanent.Name} does not have a basic land mana ability.");

        var player = state.GetPlayer(playerId);
        if (!player.ManaPool.Amounts.ContainsKey(color) || player.ManaPool.Amounts[color] <= 0)
            throw new InvalidOperationException("Cannot untap — the mana from this land has already been spent.");

        return state
            .UpdatePermanent(permanent.Untap())
            .UpdatePlayer(player with { ManaPool = player.ManaPool.Remove(color) });
    }

    // =========================================================
    // Casting spells
    // =========================================================

    public static GameState CastSpell(GameState state, Guid playerId, Guid cardId, IReadOnlyList<Target>? targets = null)
    {
        var player = state.GetPlayer(playerId);

        if (state.PriorityPlayerId != playerId)
            throw new InvalidOperationException("You do not have priority.");

        var card = player.Hand.FirstOrDefault(c => c.CardId == cardId)
            ?? throw new InvalidOperationException($"Card {cardId} not in hand.");

        if (card.IsLand)
            throw new InvalidOperationException("Lands are played, not cast.");

        // Speed check
        bool atSorcerySpeed = state.IsStackEmpty
            && state.ActivePlayerId == playerId
            && (state.CurrentPhase == Phase.PreCombatMain || state.CurrentPhase == Phase.PostCombatMain);

        bool hasFlash = card.Definition.HasKeyword(Domain.Enums.KeywordAbility.Flash);

        if (card.Definition.CastingSpeed == Domain.Enums.SpeedRestriction.Sorcery && !atSorcerySpeed && !hasFlash)
            throw new InvalidOperationException($"{card.Name} can only be cast at sorcery speed.");

        // Pay mana cost
        if (!card.ManaCost.CanBePaidBy(player.ManaPool))
            throw new InvalidOperationException($"Insufficient mana to cast {card.Name}.");

        var updatedPlayer = player
            .RemoveCardFromHand(cardId) with { ManaPool = player.ManaPool.Pay(card.ManaCost) };

        var spell = new SpellOnStack
        {
            ControllerId = playerId,
            SourceCard = card,
            Targets = targets?.ToList() ?? [],
        };

        return state
            .UpdatePlayer(updatedPlayer)
            .PushStack(spell);
    }

    // =========================================================
    // Stack resolution
    // =========================================================

    /// <summary>
    /// Resolves the top object of the stack.
    /// Returns the resulting game state.
    /// </summary>
    public static GameState ResolveTopOfStack(GameState state)
    {
        if (state.IsStackEmpty)
            throw new InvalidOperationException("Cannot resolve: stack is empty.");

        state = state.PopStack(out var top);

        return top switch
        {
            SpellOnStack spell => ResolveSpell(state, spell),
            ActivatedAbilityOnStack activated => ResolveActivatedAbility(state, activated),
            TriggeredAbilityOnStack triggered => ResolveTriggeredAbility(state, triggered),
            _ => throw new InvalidOperationException($"Unknown stack object type: {top.GetType().Name}")
        };
    }

    private static GameState ResolveSpell(GameState state, SpellOnStack spell)
    {
        var card = spell.SourceCard;

        if (card.IsPermanentType)
        {
            // Permanent spells enter the battlefield
            var permanent = CreatePermanent(card, spell.ControllerId);
            state = state.AddPermanent(permanent);
        }
        else
        {
            // Instant / sorcery goes to graveyard after resolving
            // TODO: Apply the spell's effect (requires ability system)
            var owner = state.GetPlayer(card.OwnerId);
            state = state.UpdatePlayer(owner.SendToGraveyard(card));
        }

        return state;
    }

    private static GameState ResolveActivatedAbility(GameState state, ActivatedAbilityOnStack ability)
    {
        // TODO: Execute ability effect
        return state;
    }

    private static GameState ResolveTriggeredAbility(GameState state, TriggeredAbilityOnStack triggered)
    {
        // TODO: Execute triggered ability effect
        return state;
    }

    // =========================================================
    // Permanent removal
    // =========================================================

    public static GameState DestroyPermanent(GameState state, Guid permanentId)
    {
        var permanent = state.GetPermanent(permanentId);

        if (permanent.HasKeyword(Domain.Enums.KeywordAbility.Indestructible))
            return state; // Indestructible permanents can't be destroyed

        var owner = state.GetPlayer(permanent.SourceCard.OwnerId);
        return state
            .RemovePermanent(permanentId)
            .UpdatePlayer(owner.SendToGraveyard(permanent.SourceCard));
    }

    public static GameState ExilePermanent(GameState state, Guid permanentId)
    {
        var permanent = state.GetPermanent(permanentId);
        var owner = state.GetPlayer(permanent.SourceCard.OwnerId);
        return state
            .RemovePermanent(permanentId)
            .UpdatePlayer(owner.SendToExile(permanent.SourceCard));
    }

    public static GameState BounceToHand(GameState state, Guid permanentId)
    {
        var permanent = state.GetPermanent(permanentId);
        var owner = state.GetPlayer(permanent.SourceCard.OwnerId);
        return state
            .RemovePermanent(permanentId)
            .UpdatePlayer(owner.AddCardToHand(permanent.SourceCard));
    }

    // =========================================================
    // Helpers
    // =========================================================

    private static Permanent CreatePermanent(Card card, Guid controllerId) => new()
    {
        SourceCard = card,
        ControllerId = controllerId,
        IsTapped = false,
        HasSummoningSickness = card.Definition.IsCreature && !card.Definition.HasKeyword(Domain.Enums.KeywordAbility.Haste),
    };
}
