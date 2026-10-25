using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>Ports Compleat's account lifecycle onto the existing users and SQLite database.</summary>
public sealed partial class AccountManagementService(
    MtgEngineDbContext db, TokenService tokens, IAccountEmailSender email, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string NormalizeEmail(string value) => value.Trim().ToLowerInvariant();
    private static string NewRefresh() => Convert.ToHexString(RandomNumberGenerator.GetBytes(48));

    public async Task<AccountSignIn> RegisterAsync(RegisterRequest request, string device, CancellationToken ct)
    {
        if (!request.AcceptedTerms)
            throw new InvalidRequestException("Accept the terms of use before creating an account.");
        var name = Username(request.Username);
        var address = NormalizeEmail(request.Email);
        if (await db.Users.AnyAsync(u => u.Username == name || u.Email == address, ct).ConfigureAwait(false))
            throw new InvalidResourceStateException("Username or email is already taken.");
        var user = new User { Username = name, Email = address, PasswordHash = PasswordHasher.Hash(request.Password), CreatedAt = Now };
        db.Users.Add(user);
        try
        { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
        catch (DbUpdateException) { throw new InvalidResourceStateException("Username or email is already taken."); }
        await IssueCodeAsync(user, "verify", ct).ConfigureAwait(false);
        return await StartAsync(user, device, ct).ConfigureAwait(false);
    }

    public async Task<AccountSignIn> LoginAsync(LoginRequest request, string device, CancellationToken ct)
    {
        var login = request.Username.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == login || u.Email == login, ct).ConfigureAwait(false);
        if (user is null)
        {
            PasswordHasher.VerifyDummy(request.Password);
            throw new AccountAuthenticationException("Invalid credentials.");
        }
        RequirePassword(user, request.Password);
        return await StartAsync(user, device, ct).ConfigureAwait(false);
    }

    public async Task<AccountDetails> GetAsync(Guid id, CancellationToken ct)
    {
        var user = await LoadAsync(id, ct).ConfigureAwait(false);
        return new(user.Id, user.Username, user.Email, user.EmailVerifiedAt.HasValue, user.CreatedAt);
    }

    private async Task<User> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false)
            ?? throw new AccountAuthenticationException("Sign in again to continue.");

    private static void RequirePassword(User user, string password)
    {
        if (!PasswordHasher.Verify(password, user.PasswordHash))
            throw new AccountAuthenticationException("Invalid credentials.");
    }

    public async Task<bool> ValidateAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            return false;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false);
        if (user is null)
            return false;
        var version = principal.FindFirstValue("ver") ?? "0";
        if (version != user.TokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return false;
        var session = principal.FindFirstValue("session");
        return session is null or "legacy" || (Guid.TryParse(session, out var family) && await db.AccountSessions
            .AnyAsync(s => s.FamilyId == family && s.UserId == id && s.RevokedAt == null && s.ExpiresAt > Now, ct).ConfigureAwait(false));
    }
}

public sealed class AccountAuthenticationException(string message) : Exception(message);
