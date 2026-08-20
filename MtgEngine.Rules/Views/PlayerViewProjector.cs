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
            CurrentStep = state.CurrentStep.ToString(),
            // Combat is public: who is attacking and who is blocking is visible to everyone at
            // the table (CR 506.1 happens in the open).
            Attackers = state.Combat.Attackers.ToImmutableDictionary(
                kv => kv.Key.Value, kv => kv.Value),
            Blockers = state.Combat.Blockers.ToImmutableDictionary(
                kv => kv.Key.Value, kv => kv.Value.Select(b => b.Value).ToImmutableList()),
            Players = [.. state.TurnOrder.Select(id => ProjectPlayer(state, id, viewer))],
            Battlefield = ProjectZone(state, state.Battlefield),
            Stack = ProjectZone(state, state.Stack),
            Exile = ProjectZone(state, state.Exile),
            Command = ProjectZone(state, state.Command),
            Choice = ProjectChoice(state, viewer),
        };
    }

    /// <summary>
    /// The name of a commander, found wherever it currently is.
    /// </summary>
    /// <remarks>
    /// Being a commander belongs to the card and survives every zone change (CR 903.3), so the
    /// search covers every object rather than one zone. Falls back to the oracle id, which is
    /// worse to read but never wrong.
    /// </remarks>
    private static string NameOfCommander(GameState state, string oracleId)
    {
        foreach (var (_, obj) in state.Objects)
        {
            if (string.Equals(obj.Card.OracleId, oracleId, StringComparison.Ordinal))
                return obj.Card.Name;
        }

        return oracleId;
    }

    private static ChoiceView? ProjectChoice(GameState state, Guid viewer)
    {
        if (state.Choice is not { } choice)
            return null;

        return new ChoiceView
        {
            Id = choice.Id,
            PlayerId = choice.PlayerId,
            Kind = choice.Kind.ToString(),
            Prompt = choice.Prompt,
            MinPicks = choice.MinPicks,
            MaxPicks = choice.MaxPicks,
            IsOrdering = choice.IsOrdering,
            // The options can be hidden information — bottoming after a mulligan lists the
            // asked player's hand — so only they are sent them.
            Options = choice.PlayerId == viewer
                ? [.. choice.Options.Select(o => new ChoiceOptionView(o.Id, o.Label))]
                : null,
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
            CommanderDamage = player.CommanderDamage.ToImmutableDictionary(
                kv => NameOfCommander(state, kv.Key), kv => kv.Value, StringComparer.Ordinal),
            CommanderName = player.CommanderOracleId is { } id ? NameOfCommander(state, id) : null,
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
