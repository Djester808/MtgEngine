namespace MtgEngine.Rules.State;

/// <summary>A historical disclosure, with no object identity or link to a card's future location.</summary>
public sealed record RevealedCard(string OracleId, string Name, string? ManaCost, string? OracleText, string? PrintingId)
{
    public string ArtworkKey => CardArtwork.Key(OracleId, PrintingId);
}

public static class CardArtwork
{
    public static string Key(string oracleId, string? printingId) =>
        printingId is { Length: > 0 } ? oracleId + "/" + printingId : oracleId;
}

public sealed record CardReveal(PlayerId By, ImmutableList<RevealedCard> Cards);
