using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Domain.ValueObjects;
using MtgEngine.Api.Dtos;

namespace MtgEngine.Api.Mapping;

/// <summary>
/// Maps domain models to DTOs for API / SignalR responses.
/// All methods are pure static — no state, no DI.
/// </summary>
public static class DomainMapper
{
    // ---- GameState ----------------------------------------

    public static GameStateDto ToDto(GameState state, Guid requestingPlayerId)
    {
        return new GameStateDto
        {
            GameId           = state.GameId.ToString(),
            Players          = state.Players.Select(p => ToPlayerDto(p, p.PlayerId == requestingPlayerId)).ToArray(),
            Battlefield      = state.Battlefield.Select(ToDto).ToArray(),
            Stack            = state.Stack.Reverse().Select(ToDto).ToArray(),
            Turn             = state.Turn,
            ActivePlayerId   = state.ActivePlayerId.ToString(),
            PriorityPlayerId = state.PriorityPlayerId.ToString(),
            CurrentPhase     = ToDto(state.CurrentPhase),
            CurrentStep      = ToDto(state.CurrentStep),
            Result           = ToDto(state.Result),
            Combat           = state.Combat is null ? null : ToDto(state.Combat),
        };
    }

    /// <summary>
    /// Produces a minimal diff between two game states.
    /// Only includes what actually changed to keep the payload small.
    /// </summary>
    public static GameStateDiffDto ToDiff(
        GameState before,
        GameState after,
        Guid requestingPlayerId)
    {
        // Changed / added permanents
        var changedPerms = after.Battlefield
            .Where(p =>
            {
                var old = before.Battlefield.FirstOrDefault(b => b.PermanentId == p.PermanentId);
                return old is null || !ArePermanantsEqual(old, p);
            })
            .Select(ToDto)
            .ToArray();

        // Removed permanents
        var removedIds = before.Battlefield
            .Where(p => !after.Battlefield.Any(a => a.PermanentId == p.PermanentId))
            .Select(p => p.PermanentId.ToString())
            .ToArray();

        // Player updates (only changed players)
        var playerUpdates = after.Players
            .Where(p =>
            {
                var old = before.Players.FirstOrDefault(b => b.PlayerId == p.PlayerId);
                return old is null || !ArePlayersEqual(old, p);
            })
            .Select(p => ToPlayerDto(p, p.PlayerId == requestingPlayerId))
            .ToArray();

        return new GameStateDiffDto
        {
            ChangedPermanents = changedPerms,
            RemovedPermanentIds = removedIds,
            Stack            = after.Stack.Reverse().Select(ToDto).ToArray(),
            PriorityPlayerId = after.PriorityPlayerId.ToString(),
            CurrentPhase     = ToDto(after.CurrentPhase),
            CurrentStep      = ToDto(after.CurrentStep),
            Result           = ToDto(after.Result),
            Combat           = after.Combat is null ? null : ToDto(after.Combat),
            PlayerUpdates    = playerUpdates,
        };
    }

    // ---- Permanent ----------------------------------------

    public static PermanentDto ToDto(Permanent p) => new()
    {
        PermanentId          = p.PermanentId.ToString(),
        SourceCard           = ToDto(p.SourceCard),
        ControllerId         = p.ControllerId.ToString(),
        IsTapped             = p.IsTapped,
        HasSummoningSickness = p.HasSummoningSickness,
        DamageMarked         = p.DamageMarked,
        Counters             = p.Counters.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        Attachments          = p.Attachments.Select(a => a.ToString()).ToArray(),
        EffectivePower       = p.EffectivePower,
        EffectiveToughness   = p.EffectiveToughness,
    };

    // ---- Card ---------------------------------------------

    public static CardDto ToDto(Card card) => ToDto(card.Definition, card.CardId, card.OwnerId);

    public static CardDto ToDto(CardDefinition def, Guid cardId, Guid ownerId) => new()
    {
        CardId          = cardId.ToString(),
        OracleId        = def.OracleId,
        Name            = def.Name,
        ManaCost        = string.IsNullOrEmpty(def.ManaCostRaw) ? def.ManaCost.ToString() : def.ManaCostRaw,
        ManaValue       = def.ManaCost.ManaValue,
        CardTypes       = ToCardTypeDto(def.CardTypes),
        Subtypes        = def.Subtypes.ToArray(),
        Supertypes      = def.Supertypes.ToArray(),
        OracleText      = def.OracleText,
        Power           = def.Power,
        Toughness       = def.Toughness,
        StartingLoyalty = def.StartingLoyalty,
        Keywords        = def.Keywords.ToString().Split(',').Select(s => s.Trim()).Where(s => s != "None").ToArray(),
        ImageUriNormal  = def.ImageUriNormal,
        ImageUriSmall   = def.ImageUriSmall,
        ImageUriArtCrop = def.ImageUriArtCrop,
        ColorIdentity   = def.ColorIdentity.Select(ToDto).ToArray(),
        OwnerId         = ownerId.ToString(),
        FlavorText      = def.FlavorText,
        Artist          = def.Artist,
        SetCode         = def.SetCode,
    };

    // ---- Player -------------------------------------------

    public static PlayerStateDto ToPlayerDto(PlayerState p, bool isLocal) => new()
    {
        PlayerId          = p.PlayerId.ToString(),
        Name              = p.Name,
        Life              = p.Life,
        PoisonCounters    = p.PoisonCounters,
        ManaPool          = ToDto(p.ManaPool),
        HandCount         = p.Hand.Count,
        LibraryCount      = p.Library.Count,
        GraveyardCount    = p.Graveyard.Count,
        ExileCount        = p.Exile.Count,
        HasLandPlayedThisTurn = p.HasLandPlayedThisTurn,
        // Only expose hand / graveyard / exile contents to the owning player
        Hand      = isLocal ? p.Hand.Select(c => ToDto(c)).ToArray() : [],
        Graveyard = p.Graveyard.Select(c => ToDto(c)).ToArray(),   // graveyard is public
        Exile     = p.Exile.Select(c => ToDto(c)).ToArray(),       // exile is public
    };

    // ---- Stack --------------------------------------------

    public static StackObjectDto ToDto(IStackObject obj) => obj switch
    {
        SpellOnStack spell => new StackObjectDto
        {
            StackObjectId  = spell.StackObjectId.ToString(),
            Type           = StackObjectTypeDto.Spell,
            ControllerId   = spell.ControllerId.ToString(),
            Description    = spell.Description,
            SourceCardName = spell.SourceCard.Name,
            Targets        = spell.Targets.Select(ToDto).ToArray(),
        },
        ActivatedAbilityOnStack act => new StackObjectDto
        {
            StackObjectId  = act.StackObjectId.ToString(),
            Type           = StackObjectTypeDto.ActivatedAbility,
            ControllerId   = act.ControllerId.ToString(),
            Description    = act.Description,
            SourceCardName = act.AbilityText,
            Targets        = act.Targets.Select(ToDto).ToArray(),
        },
        TriggeredAbilityOnStack trig => new StackObjectDto
        {
            StackObjectId  = trig.StackObjectId.ToString(),
            Type           = StackObjectTypeDto.TriggeredAbility,
            ControllerId   = trig.ControllerId.ToString(),
            Description    = trig.Description,
            SourceCardName = trig.TriggerText,
            Targets        = trig.Targets.Select(ToDto).ToArray(),
        },
        _ => throw new InvalidOperationException($"Unknown stack object: {obj.GetType().Name}")
    };

    public static TargetDto ToDto(Target t) => new(t.Type.ToString(), t.Id.ToString());

    // ---- Combat -------------------------------------------

    public static CombatStateDto ToDto(CombatState c) => new()
    {
        Attackers         = c.Attackers.Select(a => a.ToString()).ToArray(),
        AttackersToBlockers = c.AttackersToBlockers
            .ToDictionary(
                kv => kv.Key.ToString(),
                kv => kv.Value.Select(b => b.ToString()).ToArray()),
        AttackersDeclared = c.AttackersDeclared,
        BlockersDeclared  = c.BlockersDeclared,
    };

    // ---- Mana pool ----------------------------------------

    public static ManaPoolDto ToDto(ManaPool pool) => new()
    {
        // Use short symbols ("W","U","B","R","G","C") not enum names ("White","Blue"…)
        // so the frontend mana-cost parser ("2WW") can match pool keys directly.
        Amounts = pool.Amounts.ToDictionary(kv => ToDto(kv.Key).ToString(), kv => kv.Value),
        Total   = pool.Total,
    };

    // ---- Enum conversions ---------------------------------

    public static PhaseDto ToDto(Phase p) => p switch
    {
        Phase.Beginning      => PhaseDto.Beginning,
        Phase.PreCombatMain  => PhaseDto.PreCombatMain,
        Phase.Combat         => PhaseDto.Combat,
        Phase.PostCombatMain => PhaseDto.PostCombatMain,
        Phase.Ending         => PhaseDto.Ending,
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    public static StepDto ToDto(Step s) => s switch
    {
        Step.Untap              => StepDto.Untap,
        Step.Upkeep             => StepDto.Upkeep,
        Step.Draw               => StepDto.Draw,
        Step.Main               => StepDto.Main,
        Step.BeginningOfCombat  => StepDto.BeginningOfCombat,
        Step.DeclareAttackers   => StepDto.DeclareAttackers,
        Step.DeclareBlockers    => StepDto.DeclareBlockers,
        Step.FirstStrikeDamage  => StepDto.FirstStrikeDamage,
        Step.CombatDamage       => StepDto.CombatDamage,
        Step.EndOfCombat        => StepDto.EndOfCombat,
        Step.End                => StepDto.End,
        Step.Cleanup            => StepDto.Cleanup,
        _ => throw new ArgumentOutOfRangeException(nameof(s))
    };

    public static GameResultDto ToDto(GameResult r) => r switch
    {
        GameResult.InProgress  => GameResultDto.InProgress,
        GameResult.Player1Wins => GameResultDto.Player1Wins,
        GameResult.Player2Wins => GameResultDto.Player2Wins,
        GameResult.Draw        => GameResultDto.Draw,
        _ => throw new ArgumentOutOfRangeException(nameof(r))
    };

    public static ManaColorDto ToDto(ManaColor c) => c switch
    {
        ManaColor.White     => ManaColorDto.W,
        ManaColor.Blue      => ManaColorDto.U,
        ManaColor.Black     => ManaColorDto.B,
        ManaColor.Red       => ManaColorDto.R,
        ManaColor.Green     => ManaColorDto.G,
        ManaColor.Colorless => ManaColorDto.C,
        _ => ManaColorDto.C
    };

    public static CardTypeDto[] ToCardTypeDto(CardType flags)
    {
        var result = new List<CardTypeDto>();
        if (flags.HasFlag(CardType.Creature))     result.Add(CardTypeDto.Creature);
        if (flags.HasFlag(CardType.Instant))      result.Add(CardTypeDto.Instant);
        if (flags.HasFlag(CardType.Sorcery))      result.Add(CardTypeDto.Sorcery);
        if (flags.HasFlag(CardType.Enchantment))  result.Add(CardTypeDto.Enchantment);
        if (flags.HasFlag(CardType.Artifact))     result.Add(CardTypeDto.Artifact);
        if (flags.HasFlag(CardType.Land))         result.Add(CardTypeDto.Land);
        if (flags.HasFlag(CardType.Planeswalker)) result.Add(CardTypeDto.Planeswalker);
        return result.ToArray();
    }

    // ---- Equality helpers (for diff) ----------------------

    private static bool ArePermanantsEqual(Permanent a, Permanent b) =>
        a.IsTapped             == b.IsTapped &&
        a.DamageMarked         == b.DamageMarked &&
        a.HasSummoningSickness == b.HasSummoningSickness &&
        a.ControllerId         == b.ControllerId &&
        a.Counters.Count       == b.Counters.Count;

    private static bool ArePlayersEqual(PlayerState a, PlayerState b) =>
        a.Life             == b.Life &&
        a.PoisonCounters   == b.PoisonCounters &&
        a.ManaPool.Total   == b.ManaPool.Total &&
        a.Hand.Count       == b.Hand.Count &&
        a.Library.Count    == b.Library.Count &&
        a.Graveyard.Count  == b.Graveyard.Count &&
        a.HasLandPlayedThisTurn == b.HasLandPlayedThisTurn;
}
