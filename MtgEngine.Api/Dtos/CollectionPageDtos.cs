namespace MtgEngine.Api.Dtos;

public sealed record CollectionPageQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 100;
    public string? Query { get; init; }
    public bool MatchCase { get; init; }
    public bool MatchWord { get; init; }
    public bool Regex { get; init; }
    public string? Finish { get; init; }
    public string? Condition { get; init; }
    public string? Language { get; init; }
    public string? Tag { get; init; }
    public bool DuplicatesOnly { get; init; }
    public string? Set { get; init; }
    public string[] Colors { get; init; } = [];
    public string[] Types { get; init; } = [];
    public string[] Rarities { get; init; } = [];
    public string? Cmc { get; init; }
    public string Sort { get; init; } = "name";
    public string SortDir { get; init; } = "asc";
    public string Group { get; init; } = "none";
    public bool GroupSameCard { get; init; }
}
public sealed record CollectionPageData(int Page, int PageSize, int Total, int TotalRows,
    CollectionValuation Valuation, CollectionInsightDto[] Insights, CollectionSetOption[] SetOptions,
    string? QueryError);
public sealed record CollectionValuation(decimal Value, int Priced, int Copies);
public sealed record CollectionSetOption(string Value, string Label);
public sealed record CollectionInsightPoint(string Label, decimal Value);
public sealed record CollectionInsightDto(string Title, bool Money, CollectionInsightPoint[] Points, decimal Max);
