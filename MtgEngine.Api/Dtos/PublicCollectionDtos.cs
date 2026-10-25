namespace MtgEngine.Api.Dtos;

public sealed record CollectionVisibilityRequest(bool Public);
public sealed record CollectionCountBucket(string Key, int Copies);
/// <summary>Public-safe counts only: no card names, notes, tags, purchase details, or monetary values.</summary>
public sealed record PublicCollectionSummary(int Copies, int UniquePrintings, int UniqueCards,
    CollectionCountBucket[] ByColor, CollectionCountBucket[] ByType, CollectionCountBucket[] ByRarity,
    CollectionCountBucket[] BySet, CollectionCountBucket[] ByFinish, CollectionCountBucket[] Curve);
