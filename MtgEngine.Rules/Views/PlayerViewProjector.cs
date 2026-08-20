using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Views;

/// <summary>
/// Turns the full game state into what one player may see (CR 400.2).
/// </summary>
/// <remarks>
/// The one rule this file exists to hold: <b>a <see cref="GameState"/> never reaches a client.</b>
/// Everything the transport sends goes through here first, so hidden zones are dropped once,
/// in a place with tests on it, rather than at each of the places that send something.
/// </remarks>
public static class PlayerViewProjector
{
    /// <summary>Builds the view for one seated player.</summary>
    public static GameView Project(GameState state, Guid viewer)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.Players.ContainsKey(viewer))
            throw new InvalidOperationException($"Player {viewer} is not in this game.");

        return new GameView
        {
            GameId = state.GameId,
            Viewer = viewer,
            TurnNumber = state.TurnNumber,
            ActivePlayerId = state.ActivePlayerId,
            Players = [.. state.TurnOrder.Select(id => ProjectPlayer(state, id, viewer))],
            Battlefield = ProjectZone(state, state.Battlefield),
            Stack = ProjectZone(state, state.Stack),
            Exile = ProjectZone(state, state.Exile),
            Command = ProjectZone(state, state.Command),
        };
    }

    private static PlayerView ProjectPlayer(GameState state, Guid playerId, Guid viewer)
    {
        var player = state.GetPlayer(playerId);
        var isViewer = playerId == viewer;

        return new PlayerView
        {
            PlayerId = player.PlayerId,
            Name = player.Name,
            Life = player.Life,
            PoisonCounters = player.PoisonCounters,
            // Counts only. Nobody may look at a library, not even its owner (CR 401.2).
            LibraryCount = player.Library.Count,
            HandCount = player.Hand.Count,
            Hand = isViewer ? ProjectZone(state, player.Hand) : null,
            Graveyard = ProjectZone(state, player.Graveyard),
            HasLost = player.HasLost,
            LandsPlayedThisTurn = player.LandsPlayedThisTurn,
        };
    }

    private static ImmutableList<ObjectView> ProjectZone(
        GameState state, ImmutableList<ObjectId> zone) =>
        [.. zone.Select(id => ProjectObject(state.GetObject(id)))];

    private static ObjectView ProjectObject(GameObject obj)
    {
        var card = obj.Card;

        return new ObjectView
        {
            Id = obj.Id.Value,
            Name = card.Name,
            OracleId = card.OracleId,
            ControllerId = obj.ControllerId,
            ManaCost = string.IsNullOrEmpty(card.ManaCostRaw) ? null : card.ManaCostRaw,
            TypeLine = TypeLine(card.Supertypes, card.CardTypes, card.Subtypes),
            PrintedPower = card.Power,
            PrintedToughness = card.Toughness,
            IsTapped = obj.Permanent?.IsTapped,
            HasSummoningSickness = obj.Permanent?.HasSummoningSickness,
            DamageMarked = obj.Permanent?.DamageMarked,
            Counters = obj.Permanent?.Counters,
        };
    }

    /// <summary>
    /// Rebuilds the printed type line, e.g. "Legendary Creature — Human Wizard" (CR 205.1).
    /// </summary>
    /// <remarks>
    /// Assembled from the parts rather than stored, because the parts are what the rules act on;
    /// a stored string would be a second copy to keep in step once type-changing effects land.
    /// </remarks>
    private static string TypeLine(
        IReadOnlyList<string> supertypes, CardType types, IReadOnlyList<string> subtypes)
    {
        var left = string.Join(' ', supertypes.Concat(Names(types)));
        return subtypes.Count == 0 ? left : $"{left} — {string.Join(' ', subtypes)}";
    }

    private static IEnumerable<string> Names(CardType types) =>
        Enum.GetValues<CardType>()
            .Where(t => t != CardType.None && types.HasFlag(t))
            .Select(t => t.ToString());
}
