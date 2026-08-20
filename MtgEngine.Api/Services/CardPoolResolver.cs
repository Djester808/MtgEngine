using MtgEngine.Api.Cards;

namespace MtgEngine.Api.Services;

/// <summary>
/// Ties the card pool's scripts to real oracle ids, once, at startup.
/// </summary>
/// <remarks>
/// The pool is written against card names because a name can be checked by eye and a GUID
/// cannot. Playing by name would work until somebody renamed a card, and would quietly bind the
/// wrong behaviour if two printings ever disagreed — so the names are resolved to oracle ids
/// here, against the same card database the deck builder uses.
/// <para>
/// A failure is logged and survived. The pool falls back to matching by name, so a card database
/// that is missing or still downloading costs accuracy across renames rather than the ability to
/// start a game.
/// </para>
/// </remarks>
public sealed class CardPoolResolver : BackgroundService
{
    private readonly CardPool _pool;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CardPoolResolver> _logger;

    public CardPoolResolver(
        CardPool pool, IServiceScopeFactory scopes, ILogger<CardPoolResolver> logger)
    {
        _pool = pool;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var cards = scope.ServiceProvider.GetRequiredService<BulkDataService>();

            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in _pool.Names)
            {
                stoppingToken.ThrowIfCancellationRequested();

                var card = await cards.GetByNameAsync(name).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(card?.OracleId))
                    found[name] = card.OracleId;
            }

            _pool.ResolveOracleIds(found);
            _logger.LogInformation(
                "Card pool: {Resolved} of {Total} implemented cards tied to an oracle id.",
                _pool.ResolvedCount,
                _pool.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Survivable: the pool matches by name until this succeeds, so a game can still be
            // started. Worth saying out loud, because playing by name is the weaker match.
            _logger.LogError(ex, "Could not tie the card pool to oracle ids; matching by name.");
        }
    }
}
