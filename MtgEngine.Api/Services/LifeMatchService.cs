using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface ILifeMatchService
{
    Task<MatchRecordedDto> RecordAsync(Guid callerId, RecordMatchRequest request, CancellationToken ct);

    Task<PlayerRecordDto> GetRecordAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// Stores finished games from the life counter, and reads back one player's record.
/// </summary>
/// <remarks>
/// The trust model is the whole design. Nothing refereed these games, so the record is only
/// as good as the rule that a seat is tied to an account **only** by a token that account's
/// own player produced by signing in on the counter. That confines a fabricated match to the
/// fabricator's own numbers: a player who never signs in on somebody else's device cannot
/// have a loss written against them, whatever the device claims.
/// </remarks>
public sealed class LifeMatchService : ILifeMatchService
{
    private readonly MtgEngineDbContext _db;
    private readonly TokenService _tokens;

    /// <summary>
    /// How far back a client-supplied start time is believed.
    /// </summary>
    /// <remarks>
    /// The counter runs at a table and submits when it can, so a slightly old
    /// <c>StartedAt</c> is normal — but it comes off a device clock, and an unbounded one lets
    /// a game be filed under any date at all.
    /// </remarks>
    private static readonly TimeSpan MaxBacklog = TimeSpan.FromDays(2);

    public LifeMatchService(MtgEngineDbContext db, TokenService tokens)
    {
        _db = db;
        _tokens = tokens;
    }

    public async Task<MatchRecordedDto> RecordAsync(Guid callerId, RecordMatchRequest request, CancellationToken ct)
    {
        var seats = request.Seats;

        if (seats.Select(s => s.Seat).Distinct().Count() != seats.Length)
            throw new InvalidRequestException("Two players were given the same seat.");

        // A game ends when one player is left (CR 104.2a) or when everyone loses at once
        // (CR 104.4b covers the draw). Two winners is neither, and this counter has no
        // concept of teams, so it is a client bug rather than a table that happened.
        if (seats.Count(s => s.Won) > 1)
            throw new InvalidRequestException("A game cannot have more than one winner.");

        var attribution = await ResolveSeatOwnersAsync(seats, ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var startedAt = request.StartedAt is { } claimed
            && claimed <= now
            && claimed >= now - MaxBacklog
                ? claimed
                : now;

        var match = new LifeMatch
        {
            RecordedByUserId = callerId,
            StartedAt = startedAt,
            RecordedAt = now,
            StartingLife = request.StartingLife,
            Seats = seats
                .Select(s => new LifeMatchSeat
                {
                    Seat = s.Seat,
                    UserId = attribution.TryGetValue(s.Seat, out var owner) ? owner.UserId : null,
                    DisplayName = s.DisplayName.Trim(),
                    Won = s.Won,
                    LossReason = s.Won ? MatchLossReason.Unspecified : ParseReason(s.LossReason),
                    FinalLife = s.FinalLife,
                })
                .ToList(),
        };

        _db.LifeMatches.Add(match);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new MatchRecordedDto(
            match.Id,
            attribution.OrderBy(kv => kv.Key).Select(kv => kv.Value.Username).ToArray());
    }

    public async Task<PlayerRecordDto> GetRecordAsync(Guid userId, CancellationToken ct)
    {
        var recent = await _db.LifeMatchSeats
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.Match!.RecordedAt)
            .Select(s => new
            {
                s.MatchId,
                s.Match!.RecordedAt,
                s.Won,
                s.LossReason,
                s.FinalLife,
                // Projected in the same query rather than fetched per row: a 20-game history
                // was otherwise 21 round trips for a list of names.
                Others = s.Match.Seats
                    .Where(o => o.Id != s.Id)
                    .OrderBy(o => o.Seat)
                    .Select(o => o.DisplayName)
                    .ToList(),
            })
            .Take(LifeMatchLimits.RecentMatches)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Counted over every row, not over the page above — a record that only reflected the
        // last twenty games would silently reset itself as someone kept playing.
        var played = await _db.LifeMatchSeats
            .AsNoTracking()
            .CountAsync(s => s.UserId == userId, ct)
            .ConfigureAwait(false);

        var wins = await _db.LifeMatchSeats
            .AsNoTracking()
            .CountAsync(s => s.UserId == userId && s.Won, ct)
            .ConfigureAwait(false);

        return new PlayerRecordDto(
            played,
            wins,
            played - wins,
            played == 0 ? 0 : Math.Round((double)wins / played, 3),
            recent
                .Select(m => new MatchSummaryDto(
                    m.MatchId,
                    m.RecordedAt,
                    m.Others.Count + 1,
                    m.Won,
                    m.LossReason.ToString(),
                    m.FinalLife,
                    [.. m.Others]))
                .ToArray());
    }

    /// <summary>
    /// Turns each seat's token into the account it belongs to, refusing the whole request
    /// rather than quietly dropping one.
    /// </summary>
    /// <remarks>
    /// Silently downgrading an unreadable token to "guest" would be the worse failure: the
    /// player watches themselves sign in, wins, and finds nothing on their record with nothing
    /// having reported a problem. A 30-day token expiring mid-session is exactly the case, and
    /// the client's answer is to have that seat sign in again.
    /// </remarks>
    private async Task<Dictionary<int, (Guid UserId, string Username)>> ResolveSeatOwnersAsync(
        RecordMatchSeatRequest[] seats,
        CancellationToken ct)
    {
        var byUser = new Dictionary<Guid, int>();
        var owners = new Dictionary<int, (Guid UserId, string Username)>();

        foreach (var seat in seats)
        {
            if (string.IsNullOrWhiteSpace(seat.Token))
                continue;

            if (!_tokens.TryReadUserId(seat.Token, out var userId))
                throw new InvalidRequestException(
                    $"The sign-in for seat {seat.Seat + 1} is no longer valid. Have that player sign in again.");

            if (!byUser.TryAdd(userId, seat.Seat))
                throw new InvalidRequestException("One account cannot hold two seats in the same game.");

            owners[seat.Seat] = (userId, string.Empty);
        }

        if (owners.Count == 0)
            return owners;

        // One lookup for every seat, and it is not decoration: a token stays cryptographically
        // valid after its account is deleted, and writing a seat against a user id that no
        // longer exists would leave a row nothing can ever read back.
        var ids = owners.Values.Select(o => o.UserId).ToList();
        var names = await _db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct)
            .ConfigureAwait(false);

        foreach (var seat in owners.Keys.ToList())
        {
            if (!names.TryGetValue(owners[seat].UserId, out var username))
                throw new InvalidRequestException(
                    $"The account signed in at seat {seat + 1} no longer exists.");

            owners[seat] = (owners[seat].UserId, username);
        }

        return owners;
    }

    /// <summary>
    /// A reason name off the wire, or <see cref="MatchLossReason.Unspecified"/>.
    /// </summary>
    /// <remarks>
    /// Case-insensitive but checked against the defined members: <c>Enum.TryParse</c> accepts
    /// "7" for a value no member has, which would store a reason the enum cannot name back.
    /// </remarks>
    private static MatchLossReason ParseReason(string? name) =>
        Enum.TryParse<MatchLossReason>(name, ignoreCase: true, out var reason)
        && Enum.IsDefined(reason)
            ? reason
            : MatchLossReason.Unspecified;
}
