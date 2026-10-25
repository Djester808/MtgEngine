namespace MtgEngine.Domain.Models;

public sealed class PersistedGameLobby
{
    public Guid Id { get; set; }

    public string StateJson { get; set; } = string.Empty;

    public long Revision { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<GameLobbyMember> Members { get; set; } = [];
}

public sealed class GameLobbyMember
{
    public Guid LobbyId { get; set; }

    public string UserId { get; set; } = string.Empty;
}
