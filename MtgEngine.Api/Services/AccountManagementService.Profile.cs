using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Dtos;
namespace MtgEngine.Api.Services;

public sealed partial class AccountManagementService
{
    private static string Username(string value)
    {
        var name = value.Trim();
        if (name.Length < 3 || name.Length > 32 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new InvalidRequestException("Use 3–32 letters, numbers or underscores for your username.");
        if (new[] { "admin", "administrator", "support", "compleat", "system", "moderator" }.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith("deleted_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidRequestException("That username is reserved.");
        return name;
    }
    public async Task<UsernameAvailability> UsernameAvailableAsync(Guid id, string requested, CancellationToken ct)
    {
        var name = Username(requested).ToLowerInvariant();
        return new(!await db.Users.AnyAsync(u => u.Id != id && u.Username.ToLower() == name, ct));
    }
    public async Task ChangeUsernameAsync(Guid id, ChangeUsernameRequest request, CancellationToken ct)
    {
        var name = Username(request.Username);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LoadAsync(id, ct);
        if (!PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            throw new InvalidRequestException("Current password is incorrect.");
        if (user.Username == name)
            return;
        if (user.UsernameChangedAt is { } changed && changed > Now.AddDays(-30))
            throw new InvalidResourceStateException("You can change your username once every 30 days.");
        if (!(await UsernameAvailableAsync(id, name, ct)).Available)
            throw new InvalidResourceStateException("That username is taken.");
        user.Username = name;
        user.UsernameChangedAt = Now;
        var owner = id.ToString();
        await db.ForumPosts.Where(p => p.AuthorId == owner).ExecuteUpdateAsync(p => p.SetProperty(x => x.AuthorUsername, name), ct);
        await db.ForumComments.Where(p => p.AuthorId == owner).ExecuteUpdateAsync(p => p.SetProperty(x => x.AuthorUsername, name), ct);
        try
        { await RevokeAllAsync(id, ct); }
        catch (DbUpdateException) { throw new InvalidResourceStateException("That username is taken."); }
        await tx.CommitAsync(ct);
    }
    public async Task<DeckPrivacy> GetDeckPrivacyAsync(Guid id, CancellationToken ct) => new((await LoadAsync(id, ct)).HideProfileDecks);
    public async Task<DeckPrivacy> SetDeckPrivacyAsync(Guid id, DeckPrivacy request, CancellationToken ct)
    {
        var user = await LoadAsync(id, ct);
        user.HideProfileDecks = request.HideProfileDecks;
        await db.SaveChangesAsync(ct);
        return new(user.HideProfileDecks);
    }
}
