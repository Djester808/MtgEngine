namespace MtgEngine.Domain.Models;

public sealed class ForumPost
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeckId { get; set; }
    public string AuthorId { get; set; } = string.Empty;
    public string AuthorUsername { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ColorIdentityJson { get; set; } = "[]";
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ForumComment> Comments { get; set; } = [];
}

public sealed class ForumComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ForumPostId { get; set; }
    public ForumPost ForumPost { get; set; } = null!;
    public string AuthorId { get; set; } = string.Empty;
    public string AuthorUsername { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
