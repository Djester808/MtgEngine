using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Domain.ValueObjects;
using System.Collections.Immutable;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Factory helpers for building test game states and cards quickly.
/// </summary>
public static class TestFactory
{
    public static readonly Guid Player1Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid Player2Id = Guid.Parse("00000000-0000-0000-0000-000000000002");

    public static CardDefinition MakeCreatureDef(
        string name = "Test Creature",
        int power = 2,
        int toughness = 2,
        string manaCost = "1G",
        KeywordAbility keywords = KeywordAbility.None)
    => new()
    {
        OracleId = Guid.NewGuid().ToString(),
        Name = name,
        ManaCost = ManaCost.Parse(manaCost),
        CardTypes = CardType.Creature,
        Subtypes = ["Beast"],
        Power = power,
        Toughness = toughness,
        Keywords = keywords,
        CastingSpeed = SpeedRestriction.Sorcery,
    };

    public static CardDefinition MakeLandDef(string name = "Forest") => new()
    {
        OracleId = Guid.NewGuid().ToString(),
        Name = name,
        ManaCost = ManaCost.Zero,
        CardTypes = CardType.Land,
        Subtypes = ["Forest"],
        CastingSpeed = SpeedRestriction.Sorcery,
    };

    public static CardDefinition MakeInstantDef(string name = "Test Instant", string manaCost = "1U") => new()
    {
        OracleId = Guid.NewGuid().ToString(),
        Name = name,
        ManaCost = ManaCost.Parse(manaCost),
        CardTypes = CardType.Instant,
        CastingSpeed = SpeedRestriction.Instant,
    };

    public static Card MakeCard(CardDefinition def, Guid ownerId) => new()
    {
        CardId = Guid.NewGuid(),
        Definition = def,
        OwnerId = ownerId,
    };

    public static Permanent MakePermanent(
        CardDefinition def,
        Guid controllerId,
        bool tapped = false,
        bool summoningSick = false,
        int damage = 0,
        Dictionary<CounterType, int>? counters = null) => new()
    {
        PermanentId = Guid.NewGuid(),
        SourceCard = MakeCard(def, controllerId),
        ControllerId = controllerId,
        IsTapped = tapped,
        HasSummoningSickness = summoningSick,
        DamageMarked = damage,
        Counters = counters ?? new Dictionary<CounterType, int>(),
    };

    public static PlayerState MakePlayer(Guid playerId, string name = "Player", int life = 20) => new()
    {
        PlayerId = playerId,
        Name = name,
        Life = life,
        Hand = ImmutableList<Card>.Empty,
        Library = ImmutableList<Card>.Empty,
        Graveyard = ImmutableList<Card>.Empty,
    };

    public static GameState MakeTwoPlayerGame(
        Phase phase = Phase.PreCombatMain,
        Step step = Step.Main) => new()
    {
        GameId = Guid.NewGuid(),
        Players = ImmutableList.Create(
            MakePlayer(Player1Id, "Alice"),
            MakePlayer(Player2Id, "Bob")),
        ActivePlayerId = Player1Id,
        PriorityPlayerId = Player1Id,
        CurrentPhase = phase,
        CurrentStep = step,
        Turn = 1,
        IsFirstTurn = false,
    };

    public static GameState WithPermanent(this GameState state, Permanent permanent) =>
        state.AddPermanent(permanent);

    public static GameState WithCardInHand(this GameState state, Guid playerId, Card card)
    {
        var player = state.GetPlayer(playerId);
        return state.UpdatePlayer(player.AddCardToHand(card));
    }

    public static GameState WithMana(this GameState state, Guid playerId, ManaColor color, int amount = 1)
    {
        var player = state.GetPlayer(playerId);
        var updated = player;
        for (int i = 0; i < amount; i++)
            updated = updated.AddMana(color);
        return state.UpdatePlayer(updated);
    }
}
