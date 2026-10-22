using System.ComponentModel.DataAnnotations;

namespace MtgEngine.Api.Dtos;

public sealed record CollectionMetadataRequest(
    [Required, StringLength(8, MinimumLength = 2)] string Language,
    [StringLength(8)] string? Condition,
    [Range(typeof(decimal), "0", "21474836.47")] decimal? PurchasePrice,
    [StringLength(3, MinimumLength = 3)] string? Currency,
    [MaxLength(20)] string[] Tags,
    [StringLength(8)] string? Finish = null,
    [StringLength(4000)] string? Notes = null);
public sealed record CollectionBulkRequest(
    [Required, MinLength(1), MaxLength(500)] Guid[] CardIds,
    [Required, RegularExpression("^(tag|untag|condition|remove)$")] string Action,
    [StringLength(64)] string? Tag = null,
    [StringLength(8)] string? Condition = null);
public sealed record CollectionBulkResponse(Guid UndoToken, DateTime ExpiresAt, int Affected);

public sealed record CollectionFinishSummary(string? Condition, string[] Tags, DateTime? AddedAt, decimal? PriceAtAdd);
