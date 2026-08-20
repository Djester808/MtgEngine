using System.ComponentModel.DataAnnotations;

namespace MtgEngine.Api.Dtos;

/// <summary>Who is playing, and with what.</summary>
public sealed record CreateGameRequest
{
    /// <summary>The caller's deck.</summary>
    [Required]
    public Guid DeckId { get; init; }

    /// <summary>The opponent, who must have a deck of their own.</summary>
    [Required]
    public Guid OpponentUserId { get; init; }

    [Required]
    public Guid OpponentDeckId { get; init; }

    /// <summary>Starting life. 20 for a duel, 40 for Commander (CR 103.4, 903.7).</summary>
    [Range(1, 200)]
    public int StartingLife { get; init; } = 20;
}

public sealed record GameStartedDto(Guid GameId);
