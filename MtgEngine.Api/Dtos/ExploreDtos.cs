namespace MtgEngine.Api.Dtos;

public sealed record ExploreEntry(string Name, CardDto? Card, double? Synergy = null,
    int? Decks = null, int? Potential = null, double? Inclusion = null,
    double? Trend = null, int Quantity = 1, string Kind = "card", int? Rank = null);
public sealed record ExploreSection(string Tag, string Title, ExploreEntry[] Cards);
public sealed record ExploreVariant(string Key, string Label, int? Decks = null);
public sealed record ExploreChart(string Title, ExploreChartPoint[] Points);
public sealed record ExploreChartPoint(string Label, double Value);
public sealed record ExplorePage(bool HasPage, string Source, DateTimeOffset FetchedAt,
    string Kind, CardDto Subject, string Variant, int? DeckCount, int? PotentialDecks,
    int? Rank, decimal? Salt, ExploreSection[] Sections, ExploreVariant[] Variants,
    ExploreEntry[] Similar, ExploreEntry[] Commanders, ExploreChart[] Charts);
public sealed record ExploreDirectory(DateTimeOffset FetchedAt, ExploreEntry[] Commanders, int Total = 0, int Page = 1, int PageSize = 100, string Window = "all");
