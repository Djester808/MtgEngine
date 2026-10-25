using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

/// <summary>Local-only artwork failure fixture; never queries a catalog or external service.</summary>
public sealed class QaUnavailableArtwork : ICardLookup
{
    public Task<CardDefinition?> GetLocalByOracleIdAsync(string oracleId) => Task.FromResult<CardDefinition?>(
        new CardDefinition { OracleId = oracleId, ImageUriNormal = "/api/qa/unavailable-art" });
    public Task<CardDefinition?> GetByOracleIdAsync(string oracleId) => throw new NotSupportedException();
    public Task<CardDefinition?> GetByNameAsync(string name) => throw new NotSupportedException();
    public Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId) => throw new NotSupportedException();
    public Task<PrintingDto[]> GetPrintingsAsync(string oracleId) => throw new NotSupportedException();
    public Task<RulingDto[]> GetRulingsAsync(string oracleId) => throw new NotSupportedException();
    public Task<CardDefinition[]> SearchAsync(string query, int limit = 20, int offset = 0,
        string sortBy = "name", string sortDir = "asc", bool matchCase = false,
        bool matchWord = false, bool useRegex = false) => throw new NotSupportedException();
}
