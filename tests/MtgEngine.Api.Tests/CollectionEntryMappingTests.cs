using MtgEngine.Api.Mapping;
using MtgEngine.Api.Services;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The collection-entry mapping and its definition lookup were previously copied into
/// six call sites; two resolved by oracle id even when the row pinned a printing, and
/// only some normalized the board. These tests pin the unified behavior.
/// </summary>
public class CollectionEntryMappingTests
{
    private sealed class FakeLookup : ICardLookup
    {
        public Dictionary<string, CardDefinition> ByScryfallId { get; } = new();
        public Dictionary<string, CardDefinition> ByOracleId { get; } = new();

        public Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId) =>
            Task.FromResult(ByScryfallId.TryGetValue(scryfallId, out var d) ? d : null);

        public Task<CardDefinition?> GetByOracleIdAsync(string oracleId) =>
            Task.FromResult(ByOracleId.TryGetValue(oracleId, out var d) ? d : null);

        public Task<CardDefinition?> GetByNameAsync(string name) => throw new NotSupportedException();
        public Task<PrintingDto[]> GetPrintingsAsync(string oracleId) => throw new NotSupportedException();
        public Task<RulingDto[]> GetRulingsAsync(string oracleId) => throw new NotSupportedException();
        public Task<CardDefinition[]> SearchAsync(
            string query, int limit = 20, int offset = 0, string sortBy = "name",
            string sortDir = "asc", bool matchCase = false, bool matchWord = false,
            bool useRegex = false) => throw new NotSupportedException();
    }

    private static CollectionCard Entry(string? scryfallId = null, string board = "main") => new()
    {
        OracleId = "oracle-1",
        ScryfallId = scryfallId,
        Quantity = 2,
        QuantityFoil = 1,
        Notes = "keeper",
        Board = board,
    };

    private static CardDefinition Def(string name) => new()
    {
        OracleId = "oracle-1",
        Name = name,
        CardTypes = CardType.Creature,
    };

    [Fact]
    public void ToDto_MapsEntryFieldsAndDetails()
    {
        var entry = Entry(scryfallId: "scry-1");
        var dto = DomainMapper.ToDto(entry, Def("Krenko, Mob Boss"));

        Assert.Equal(entry.Id, dto.Id);
        Assert.Equal("oracle-1", dto.OracleId);
        Assert.Equal("scry-1", dto.ScryfallId);
        Assert.Equal(2, dto.Quantity);
        Assert.Equal(1, dto.QuantityFoil);
        Assert.Equal("keeper", dto.Notes);
        Assert.Equal("main", dto.Board);
        Assert.NotNull(dto.CardDetails);
        Assert.Equal("Krenko, Mob Boss", dto.CardDetails!.Name);
    }

    [Fact]
    public void ToDto_NullDefinition_LeavesDetailsNull()
    {
        Assert.Null(DomainMapper.ToDto(Entry(), null).CardDetails);
    }

    [Theory]
    [InlineData("side", "side")]
    [InlineData("maybe", "maybe")]
    [InlineData("", "main")]
    [InlineData("attic", "main")]
    public void ToDto_NormalizesBoard(string stored, string expected)
    {
        Assert.Equal(expected, DomainMapper.ToDto(Entry(board: stored), null).Board);
    }

    [Fact]
    public async Task ResolveForEntry_PinnedPrintingWins()
    {
        var lookup = new FakeLookup();
        lookup.ByScryfallId["scry-1"] = Def("Pinned Printing");
        lookup.ByOracleId["oracle-1"] = Def("Default Printing");

        var def = await lookup.ResolveForEntryAsync(Entry(scryfallId: "scry-1"));
        Assert.Equal("Pinned Printing", def!.Name);
    }

    [Fact]
    public async Task ResolveForEntry_UnresolvablePin_FallsBackToOracle()
    {
        var lookup = new FakeLookup();
        lookup.ByOracleId["oracle-1"] = Def("Default Printing");

        var def = await lookup.ResolveForEntryAsync(Entry(scryfallId: "scry-gone"));
        Assert.Equal("Default Printing", def!.Name);
    }

    [Fact]
    public async Task ResolveForEntry_NoPin_UsesOracleDefault()
    {
        var lookup = new FakeLookup();
        lookup.ByOracleId["oracle-1"] = Def("Default Printing");

        var def = await lookup.ResolveForEntryAsync(Entry());
        Assert.Equal("Default Printing", def!.Name);
    }
}
