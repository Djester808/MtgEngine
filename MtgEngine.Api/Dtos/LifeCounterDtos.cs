using System.ComponentModel.DataAnnotations;

namespace MtgEngine.Api.Dtos;

/// <summary>The most seats a recorded game can have. Matches the counter's own cap.</summary>
public static class LifeMatchLimits
{
    public const int MinSeats = 2;
    public const int MaxSeats = 8;

    /// <summary>How many recent games <c>GET /api/matches/me</c> carries.</summary>
    public const int RecentMatches = 20;
}

/// <summary>
/// One seat's outcome, as the life counter saw it.
/// </summary>
public sealed record RecordMatchSeatRequest
{
    [Range(0, LifeMatchLimits.MaxSeats - 1)]
    public int Seat { get; init; }

    /// <summary>What the counter showed above the seat. Stored for guests as well as accounts.</summary>
    [Required]
    [StringLength(40, MinimumLength = 1)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// The bearer token this seat's player got by signing in on the counter, or null for a guest.
    /// </summary>
    /// <remarks>
    /// Deliberately a token and not a user id. The counter runs on one shared device, so
    /// "who is in seat 3" is a claim the device makes — and a device that could name any
    /// account would be able to write to any stranger's win/loss record. Holding the seat's
    /// own token is the only proof available at a kitchen table, so it is the proof required.
    /// The token travels in the body rather than a header because a single request carries up
    /// to eight of them; it is the same secret an <c>Authorization</c> header would carry over
    /// the same TLS connection.
    /// </remarks>
    [StringLength(4096)]
    public string? Token { get; init; }

    public bool Won { get; init; }

    /// <summary>A <c>MatchLossReason</c> name. Unparseable or absent becomes <c>Unspecified</c>.</summary>
    [StringLength(32)]
    public string? LossReason { get; init; }

    [Range(-9999, 9999)]
    public int FinalLife { get; init; }
}

/// <summary>A finished game, submitted by whoever was running the counter.</summary>
public sealed record RecordMatchRequest
{
    /// <summary>Starting life the table agreed on — 20 for a duel, 40 for Commander.</summary>
    [Range(1, 999)]
    public int StartingLife { get; init; } = 40;

    /// <summary>When the game began. Ignored if it is in the future or absurdly old.</summary>
    public DateTime? StartedAt { get; init; }

    [Required]
    [MinLength(LifeMatchLimits.MinSeats)]
    [MaxLength(LifeMatchLimits.MaxSeats)]
    public RecordMatchSeatRequest[] Seats { get; init; } = [];
}

/// <summary>
/// What was actually written.
/// </summary>
/// <param name="MatchId">The stored game.</param>
/// <param name="AttributedUsernames">
/// The accounts whose record this game counts towards. A seat whose token was missing was
/// stored as a guest and is absent here — the client shows that rather than letting a player
/// believe a win was banked when it was not.
/// </param>
public sealed record MatchRecordedDto(Guid MatchId, string[] AttributedUsernames);

/// <summary>One line of a player's history.</summary>
/// <param name="Opponents">The other seats' names, in seat order.</param>
public sealed record MatchSummaryDto(
    Guid MatchId,
    DateTime RecordedAt,
    int SeatCount,
    bool Won,
    string LossReason,
    int FinalLife,
    string[] Opponents);

/// <summary>A player's standing, as reported by the counter.</summary>
/// <param name="WinRate">Wins / played, 0 when nothing has been played. Rounded to three places.</param>
public sealed record PlayerRecordDto(
    int Played,
    int Wins,
    int Losses,
    double WinRate,
    MatchSummaryDto[] Recent);
