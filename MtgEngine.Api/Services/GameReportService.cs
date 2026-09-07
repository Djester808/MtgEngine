using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>
/// Reports filed from real games: the last line of verification, and the only one that sees play.
/// </summary>
/// <remarks>
/// A report is stored, never judged. The pipeline turns it into a scenario candidate for triage,
/// where the log up to the reported index is the board and the named card is what was played.
/// Deciding here whether the player was right would need the engine to be the thing that is
/// under suspicion, which is exactly what the report says it is.
/// </remarks>
public sealed class GameReportService(MtgEngineDbContext db, GameSessionService sessions)
{
    /// <summary>
    /// Files a report against a position in a game's log.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">No such game.</exception>
    /// <exception cref="InvalidRequestException">The caller is not seated there, or the position is not in the log.</exception>
    public async Task<GameReportDto> FileAsync(Guid gameId, string userId, GameReportRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = await sessions.FindAsync(gameId, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("No such game.");

        // A seat, not a user id in the body: only someone who played can say what they saw.
        if (session.SeatOf(userId) is null)
            throw new InvalidRequestException("You are not seated at that table.");

        if (request.EventIndex >= session.Log.Count)
            throw new InvalidRequestException("That position is not in the game's log.");

        var report = new GameReport
        {
            Id = Guid.NewGuid(),
            GameId = gameId,
            UserId = userId,
            EventIndex = request.EventIndex,
            OracleId = request.OracleId,
            Note = request.Note,
        };

        db.GameReports.Add(report);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new GameReportDto(report.Id, report.GameId, report.EventIndex, report.OracleId, report.Note, report.CreatedAt);
    }
}
