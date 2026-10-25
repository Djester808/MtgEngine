using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public sealed partial class AccountManagementService
{
    private async Task<AccountSignIn> StartAsync(User user, string device, CancellationToken ct)
    {
        var refresh = NewRefresh();
        var session = NewSession(user.Id, refresh, device, Guid.NewGuid(), Now.AddDays(30));
        db.AccountSessions.Add(session);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Response(user, session, refresh);
    }

    private AccountSession NewSession(Guid userId, string refresh, string device, Guid family, DateTime expires) => new()
    {
        UserId = userId,
        FamilyId = family,
        RefreshHash = Hash(refresh),
        DeviceLabel = device[..Math.Min(device.Length, 160)],
        CreatedAt = Now,
        LastUsedAt = Now,
        ExpiresAt = expires,
    };

    private AccountSignIn Response(User user, AccountSession session, string refresh) =>
        new(new AuthTokenResponse(tokens.Generate(user, session.FamilyId), user.Username), refresh, session.ExpiresAt);

    public async Task<AccountSignIn> RefreshAsync(string refresh, CancellationToken ct)
    {
        if (refresh.Length < 64 || refresh.Length > 128)
            throw new AccountAuthenticationException("Sign in again to continue.");
        var hash = Hash(refresh);
        var session = await db.AccountSessions.AsNoTracking().FirstOrDefaultAsync(s => s.RefreshHash == hash, ct).ConfigureAwait(false);
        if (session is null || session.ExpiresAt <= Now)
            throw new AccountAuthenticationException("Sign in again to continue.");
        if (session.RevokedAt.HasValue)
        {
            await RevokeAsync(session.UserId, session.FamilyId, ct).ConfigureAwait(false);
            throw new AccountAuthenticationException("That session was reused. Sign in again.");
        }
        var user = await LoadAsync(session.UserId, ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var spent = await db.AccountSessions.Where(s => s.Id == session.Id && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, Now), ct).ConfigureAwait(false);
        if (spent == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            await RevokeAsync(session.UserId, session.FamilyId, ct).ConfigureAwait(false);
            throw new AccountAuthenticationException("That session was reused. Sign in again.");
        }
        var nextRefresh = NewRefresh();
        var next = NewSession(user.Id, nextRefresh, session.DeviceLabel, session.FamilyId, session.ExpiresAt);
        db.AccountSessions.Add(next);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return Response(user, next, nextRefresh);
    }

    public async Task<AccountSessionDetails[]> SessionsAsync(Guid id, Guid? current, CancellationToken ct) =>
        await db.AccountSessions.AsNoTracking().Where(s => s.UserId == id && s.RevokedAt == null && s.ExpiresAt > Now)
            .OrderByDescending(s => s.LastUsedAt)
            .Select(s => new AccountSessionDetails(s.FamilyId, s.DeviceLabel, s.CreatedAt, s.LastUsedAt, s.ExpiresAt, s.FamilyId == current))
            .ToArrayAsync(ct).ConfigureAwait(false);

    public Task<int> RevokeAsync(Guid id, Guid family, CancellationToken ct) => db.AccountSessions
        .Where(s => s.UserId == id && s.FamilyId == family && s.RevokedAt == null)
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, Now), ct);

    public async Task SignOutAsync(string refresh, Guid? userId, Guid? family, CancellationToken ct)
    {
        if (refresh.Length is >= 64 and <= 128)
        {
            var hash = Hash(refresh);
            var session = await db.AccountSessions.AsNoTracking().FirstOrDefaultAsync(s => s.RefreshHash == hash, ct).ConfigureAwait(false);
            if (session is not null)
                await RevokeAsync(session.UserId, session.FamilyId, ct).ConfigureAwait(false);
        }
        if (userId.HasValue && family.HasValue)
            await RevokeAsync(userId.Value, family.Value, ct).ConfigureAwait(false);
        else if (userId.HasValue)
            await RevokeAllAsync(userId.Value, ct).ConfigureAwait(false);
    }

    public async Task RevokeAllAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        var user = await LoadAsync(id, ct).ConfigureAwait(false);
        user.TokenVersion++;
        await db.AccountSessions.Where(s => s.UserId == id && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, Now), ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (transaction is not null)
            await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}
