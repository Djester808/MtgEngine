using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public sealed partial class AccountManagementService
{
    private async Task IssueCodeAsync(User user, string purpose, CancellationToken ct)
    {
        var recent = await db.AccountCodes.AnyAsync(c => c.UserId == user.Id && c.Purpose == purpose && c.CreatedAt > Now.AddMinutes(-1), ct).ConfigureAwait(false);
        if (recent)
            return;
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        await db.AccountCodes.Where(c => c.UserId == user.Id && c.Purpose == purpose && c.UsedAt == null)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.UsedAt, Now), ct).ConfigureAwait(false);
        db.AccountCodes.Add(new AccountCode
        {
            UserId = user.Id,
            Purpose = purpose,
            Hash = Hash($"{user.Id}:{purpose}:{code}"),
            CreatedAt = Now,
            ExpiresAt = Now.AddMinutes(10),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await email.SendAsync(user.Email, purpose == "verify" ? "Verify your Compleat email" : "Reset your Compleat password",
            $"Your Compleat code is {code}. It expires in 10 minutes. If you did not request this, ignore this message.", ct).ConfigureAwait(false);
    }

    private async Task<bool> RedeemCodeAsync(Guid id, string purpose, string code, CancellationToken ct)
    {
        var live = await db.AccountCodes.AsNoTracking().Where(c => c.UserId == id && c.Purpose == purpose && c.UsedAt == null && c.ExpiresAt > Now && c.Attempts < 5)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (live is null)
            return false;
        var correct = CryptographicOperations.FixedTimeEquals(Convert.FromHexString(live.Hash), Convert.FromHexString(Hash($"{id}:{purpose}:{code}")));
        var changed = await db.AccountCodes.Where(c => c.Id == live.Id && c.UsedAt == null && c.Attempts == live.Attempts)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.Attempts, live.Attempts + 1)
                .SetProperty(x => x.UsedAt, correct || live.Attempts >= 4 ? Now : (DateTime?)null), ct).ConfigureAwait(false);
        return correct && changed == 1;
    }

    public async Task ResendAsync(Guid id, CancellationToken ct)
    {
        var user = await LoadAsync(id, ct).ConfigureAwait(false);
        if (!user.EmailVerifiedAt.HasValue)
            await IssueCodeAsync(user, "verify", ct).ConfigureAwait(false);
    }

    public async Task VerifyAsync(Guid id, string code, CancellationToken ct)
    {
        var user = await LoadAsync(id, ct).ConfigureAwait(false);
        if (user.EmailVerifiedAt.HasValue)
            return;
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var valid = await RedeemCodeAsync(id, "verify", code, ct).ConfigureAwait(false);
        if (valid)
        { user.EmailVerifiedAt = Now; await db.SaveChangesAsync(ct).ConfigureAwait(false); }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        if (!valid)
            throw new InvalidRequestException("That code is incorrect or expired. Request a new code.");
    }

    public async Task ForgotAsync(string address, CancellationToken ct)
    {
        var normalized = NormalizeEmail(address);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalized, ct).ConfigureAwait(false);
        if (user is not null)
            await IssueCodeAsync(user, "reset", ct).ConfigureAwait(false);
    }

    public async Task ResetAsync(ResetAccountPasswordRequest request, CancellationToken ct)
    {
        var normalized = NormalizeEmail(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalized, ct).ConfigureAwait(false);
        if (user is null)
            throw new InvalidRequestException("That code is incorrect or expired. Request a new code.");
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var valid = await RedeemCodeAsync(user.Id, "reset", request.Code, ct).ConfigureAwait(false);
        if (valid)
        {
            user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
            await RevokeAllAsync(user.Id, ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        if (!valid)
            throw new InvalidRequestException("That code is incorrect or expired. Request a new code.");
    }

    public async Task ChangePasswordAsync(Guid id, ChangeAccountPasswordRequest request, CancellationToken ct)
    {
        var user = await LoadAsync(id, ct).ConfigureAwait(false);
        if (!PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            throw new InvalidRequestException("Current password is incorrect.");
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
        await RevokeAllAsync(id, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task ChangeEmailAsync(Guid id, ChangeAccountEmailRequest request, CancellationToken ct)
    {
        var user = await LoadAsync(id, ct).ConfigureAwait(false);
        if (!PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            throw new InvalidRequestException("Current password is incorrect.");
        var address = NormalizeEmail(request.Email);
        if (await db.Users.AnyAsync(u => u.Id != id && u.Email.ToLower() == address, ct).ConfigureAwait(false))
            throw new InvalidResourceStateException("That email address is already in use.");
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        user.Email = address;
        user.EmailVerifiedAt = null;
        await db.AccountCodes.Where(c => c.UserId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await RevokeAllAsync(id, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        await IssueCodeAsync(user, "verify", ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, string password, CancellationToken ct)
    {
        var user = await LoadAsync(id, ct).ConfigureAwait(false);
        if (!PasswordHasher.Verify(password, user.PasswordHash))
            throw new InvalidRequestException("Current password is incorrect.");
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var owner = id.ToString();
        var collections = db.Collections.Where(c => c.UserId == owner).Select(c => c.Id);
        var posts = db.ForumPosts.Where(p => p.AuthorId == owner || collections.Contains(p.DeckId)).Select(p => p.Id);
        await db.ForumComments.Where(c => c.AuthorId == owner || posts.Contains(c.ForumPostId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ForumPosts.Where(p => p.AuthorId == owner || collections.Contains(p.DeckId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.CollectionCards.Where(c => collections.Contains(c.CollectionId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Collections.Where(c => c.UserId == owner).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.UserAvatars.Where(a => a.UserId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.AccountCodes.Where(c => c.UserId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.CollectionUndos.Where(u => u.UserId == owner).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.CollectionCardEvents.Where(e => e.UserId == owner).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        user.Email = $"{id:N}@deleted.invalid";
        user.Username = $"deleted_{id:N}";
        user.PasswordHash = "DELETED";
        user.DisplayName = null;
        user.Tagline = null;
        user.Bio = null;
        user.FavoriteCommanderOracleId = null;
        user.FavoriteFormat = null;
        user.PreferencesJson = null;
        user.AvatarUpdatedAt = null;
        user.DeletedAt = Now;
        user.TokenVersion++;
        await db.AccountSessions.Where(s => s.UserId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}
