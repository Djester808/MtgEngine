using System.ComponentModel.DataAnnotations;

namespace MtgEngine.Api.Dtos;

public sealed record CreateGameLobbyRequest(
    Guid DeckId,
    [Range(2, 4)] int Capacity,
    [Required, StringLength(32)] string Format,
    [Range(1, 999)] int StartingLife,
    bool AllowHonourSystem,
    [Required, MinLength(1), MaxLength(3)] IReadOnlyList<Guid> Invitees);

public sealed record BringLobbyDeckRequest(Guid DeckId, [Range(0, long.MaxValue)] long Revision);

public sealed record LobbyRevisionRequest([Range(0, long.MaxValue)] long Revision);

public sealed record GameLobbyStartDto(Guid GameId, long Revision);

public sealed record GameLobbyPlayerDto(Guid UserId, string Username);

public sealed record GameLobbySeatDto(string UserId, string Name, bool Ready, Guid? DeckId);

public sealed record GameLobbyDto(Guid Id, string HostUserId, int Capacity, string Format, int StartingLife,
    bool AllowHonourSystem, IReadOnlyList<GameLobbySeatDto> Seats, long Revision, bool Starting, bool Cancelled, Guid? GameId)
{
    public bool IsHost { get; init; }
}
