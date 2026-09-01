using MtgEngine.Rules.Views;

namespace MtgEngine.Api.Services;

/// <summary>
/// Puts the pictures back on a game view.
/// </summary>
/// <remarks>
/// A game's log records what happened, not what a printing looked like: the card table in
/// <c>EventLogSerializer</c> carries the twelve printed fields the rules act on and nothing
/// else, deliberately, because art and prices change without the game changing. That is the
/// right shape for a stored game and the wrong shape for a screen — a game reopened after a
/// restart came back with every card's art missing, and a board of names is a board you cannot
/// play unless you already know every card on it.
/// <para>
/// So presentation is re-attached here, at the edge, from the card database. The engine never
/// sees it, the log never stores it, and a resumed game looks like the game it was.
/// </para>
/// </remarks>
public sealed class GameCardArt
{
    private readonly ICardLookup _cards;

    public GameCardArt(ICardLookup cards) => _cards = cards;

    /// <summary>Fills in art for every card in the view that is missing it.</summary>
    public async Task<GameView> FillAsync(GameView view, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(view);

        // One lookup per distinct card, not per object: a battlefield of eight Mountains is one
        // question, and a library's worth of them would otherwise be forty.
        var wanted = Missing(view).Select(o => o.OracleId).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
            return view;

        var art = new Dictionary<string, (string? Art, string? Image, string? Text)>(StringComparer.Ordinal);
        foreach (var oracleId in wanted)
        {
            ct.ThrowIfCancellationRequested();

            var card = await _cards.GetByOracleIdAsync(oracleId).ConfigureAwait(false);
            if (card is null)
                continue;

            art[oracleId] = (
                card.ImageUriArtCrop ?? card.ImageUriSmall,
                card.ImageUriNormal ?? card.ImageUriLarge,
                string.IsNullOrWhiteSpace(card.OracleText) ? null : card.OracleText);
        }

        if (art.Count == 0)
            return view;

        ObjectView Fill(ObjectView o) =>
            o.ArtUri is null && art.TryGetValue(o.OracleId, out var found)
                ? o with
                {
                    ArtUri = found.Art,
                    ImageUri = o.ImageUri ?? found.Image,
                    OracleText = o.OracleText ?? found.Text,
                }
                : o;

        return view with
        {
            Battlefield = [.. view.Battlefield.Select(Fill)],
            Stack = [.. view.Stack.Select(Fill)],
            Exile = [.. view.Exile.Select(Fill)],
            Command = [.. view.Command.Select(Fill)],
            Players =
            [
                .. view.Players.Select(p => p with
                {
                    Hand = p.Hand is null ? null : [.. p.Hand.Select(Fill)],
                    Graveyard = [.. p.Graveyard.Select(Fill)],
                }),
            ],
        };
    }

    private static IEnumerable<ObjectView> Missing(GameView view) =>
        view.Battlefield
            .Concat(view.Stack)
            .Concat(view.Exile)
            .Concat(view.Command)
            .Concat(view.Players.SelectMany(p => (p.Hand ?? []).Concat(p.Graveyard)))
            .Where(o => o.ArtUri is null && !string.IsNullOrEmpty(o.OracleId));
}
