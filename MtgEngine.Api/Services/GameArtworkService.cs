using MtgEngine.Rules.Views;

namespace MtgEngine.Api.Services;

public sealed record GameArtwork(string? ImageUri, string? ArtUri);

/// <summary>
/// Display-only art for already-visible definitions. Hidden objects never enter the input;
/// a cache miss leaves a readable card fallback rather than delaying a game command.
/// </summary>
public sealed class GameArtworkService(ICardLookup lookup)
{
    public async Task<IReadOnlyDictionary<string, GameArtwork>> ForAsync(GameView? view, CancellationToken ct)
    {
        if (view is null)
            return new Dictionary<string, GameArtwork>();
        var visible = VisibleGameObjects.Current(view);
        var result = new Dictionary<string, GameArtwork>(StringComparer.Ordinal);
        var selections = visible.Where(o => !o.FaceDown && o.OracleId.Length > 0)
            .Select(o => (o.OracleId, o.PrintingId, o.ArtworkKey))
            .Concat((view.LastReveal?.Cards ?? []).Concat(view.LastLook?.Cards ?? [])
                .Select(o => (o.OracleId, o.PrintingId, o.ArtworkKey)));
        foreach (var selected in selections.DistinctBy(o => o.ArtworkKey))
        {
            ct.ThrowIfCancellationRequested();
            var card = await lookup.ResolveSelectionAsync(selected.OracleId, selected.PrintingId, localOnly: true).ConfigureAwait(false);
            if (card is not null && (card.ImageUriNormal is not null || card.ImageUriArtCrop is not null))
                result[selected.ArtworkKey] = new GameArtwork(card.ImageUriNormal, card.ImageUriArtCrop);
        }
        return result;
    }
}
