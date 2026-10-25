using MtgEngine.Cards;

namespace MtgEngine.Api.Services;

/// <summary>Printed catalog data, without executable rules or a coverage claim.</summary>
public interface IPrintedCardLookup
{
    Task<CardDefinition?> FindPrintedAsync(string oracleId, CancellationToken ct = default);
}
