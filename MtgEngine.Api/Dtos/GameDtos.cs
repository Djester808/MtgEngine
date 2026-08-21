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

/// <summary>An invitation to a game, as the other player sees it.</summary>
public sealed record GameInviteDto(
    Guid Id,
    Guid FromUserId,
    string FromUserName,
    int StartingLife,
    DateTimeOffset CreatedUtc);

/// <summary>Inviting somebody, with the deck you intend to bring.</summary>
public sealed record CreateInviteRequest
{
    [NotEmpty]
    public Guid DeckId { get; init; }

    [NotEmpty]
    public Guid OpponentUserId { get; init; }

    /// <summary>20 for a duel, 40 for Commander (CR 103.4, 903.7).</summary>
    [Range(1, 200)]
    public int StartingLife { get; init; } = 20;
}

/// <summary>Accepting an invitation, with the deck you are bringing.</summary>
public sealed record AcceptInviteRequest
{
    [NotEmpty]
    public Guid DeckId { get; init; }
}

/// <summary>
/// A <see cref="Guid"/> that has to be a real one.
/// </summary>
/// <remarks>
/// <c>[Required]</c> does nothing on a non-nullable value type: it is always present, so
/// <c>Guid.Empty</c> passes it. An invitation whose opponent was omitted was therefore accepted
/// with a 200 and addressed to nobody — it sat in the sender's Sent list for an hour and could
/// never be answered, which is a worse outcome than a refusal.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class NotEmptyAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value switch
    {
        Guid guid => guid != Guid.Empty,
        null => false,
        _ => true,
    };

    public override string FormatErrorMessage(string name) => $"{name} is required.";
}

/// <summary>A deck, as the lobby lists it.</summary>
public sealed record PlayableDeckDto(Guid Id, string Name, int CardCount);

/// <summary>A player the caller could invite.</summary>
/// <remarks>
/// Its own shape rather than the community directory's <c>PlayerSummaryDto</c>, because the two
/// answer different questions. That one is public and anonymous and deliberately carries no user
/// id; this one is for a signed-in player picking an opponent, and an invitation needs an id to
/// address. The lobby was reading the public list and binding a field it does not have, so every
/// option in the picker was worth nothing and no invitation could be sent.
/// </remarks>
public sealed record OpponentDto(Guid UserId, string Username);
