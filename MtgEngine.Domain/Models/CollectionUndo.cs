namespace MtgEngine.Domain.Models;

public sealed class CollectionUndo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public Guid CollectionId { get; set; }
    public string BeforeJson { get; set; } = string.Empty;
    public string AfterHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}
