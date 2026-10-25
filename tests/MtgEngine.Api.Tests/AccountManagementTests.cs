using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

public sealed class AccountManagementTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly MtgEngineDbContext _db;
    private readonly AccountManagementService _accounts;
    private readonly Mailbox _mail = new();
    private readonly Clock _clock = new();

    public AccountManagementTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Secret"] = new('k', 64) }).Build();
        _accounts = new(_db, new TokenService(config), _mail, _clock);
    }

    [Fact]
    public async Task Username_change_checks_password_uniqueness_cooldown_and_revokes_sessions()
    {
        await Register();
        var user = await _db.Users.SingleAsync();
        var session = await _db.AccountSessions.SingleAsync();
        Assert.True((await _accounts.UsernameAvailableAsync(user.Id, "nissa", default)).Available);
        await Assert.ThrowsAsync<InvalidRequestException>(() => _accounts.UsernameAvailableAsync(user.Id, "bad name", default));
        await Assert.ThrowsAsync<InvalidRequestException>(() => _accounts.UsernameAvailableAsync(user.Id, "admin", default));
        await Assert.ThrowsAsync<InvalidRequestException>(() => _accounts.ChangeUsernameAsync(user.Id, new("NewNissa", "wrong"), default));
        var other = new MtgEngine.Domain.Models.User { Username = "Taken", Email = "other@example.test", PasswordHash = "x" };
        _db.Users.Add(other);
        await _db.SaveChangesAsync();
        Assert.False((await _accounts.UsernameAvailableAsync(user.Id, "tAkEn", default)).Available);
        await Assert.ThrowsAsync<InvalidResourceStateException>(() => _accounts.ChangeUsernameAsync(user.Id, new("TAKEN", "ValidPassword!123"), default));
        var deck = new MtgEngine.Domain.Models.Collection(user.Id.ToString(), "Deck", isDeck: true);
        _db.Collections.Add(deck);
        var post = new MtgEngine.Domain.Models.ForumPost { DeckId = deck.Id, AuthorId = user.Id.ToString(), AuthorUsername = user.Username };
        _db.ForumPosts.Add(post);
        _db.ForumComments.Add(new() { ForumPostId = post.Id, AuthorId = user.Id.ToString(), AuthorUsername = user.Username, Content = "Hello" });
        await _db.SaveChangesAsync();
        await _accounts.ChangeUsernameAsync(user.Id, new("NewNissa", "ValidPassword!123"), default);
        Assert.Equal("NewNissa", (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).Username);
        Assert.Equal("NewNissa", (await _db.ForumPosts.AsNoTracking().SingleAsync()).AuthorUsername);
        Assert.Equal("NewNissa", (await _db.ForumComments.AsNoTracking().SingleAsync()).AuthorUsername);
        Assert.False(await _accounts.ValidateAsync(Principal(user.Id, session.FamilyId), default));
        await Assert.ThrowsAsync<AccountAuthenticationException>(() => _accounts.LoginAsync(new("Nissa", "ValidPassword!123"), "Test", default));
        Assert.Equal("NewNissa", (await _accounts.LoginAsync(new("NewNissa", "ValidPassword!123"), "Test", default)).Response.Username);
        await Assert.ThrowsAsync<InvalidResourceStateException>(() => _accounts.ChangeUsernameAsync(user.Id, new("ThirdNissa", "ValidPassword!123"), default));
        _clock.Advance(TimeSpan.FromDays(31));
        await _accounts.ChangeUsernameAsync(user.Id, new("ThirdNissa", "ValidPassword!123"), default);
        Assert.Equal("ThirdNissa", user.Username);
    }
    [Fact]
    public async Task Deck_privacy_is_owned_and_persisted()
    {
        await Register();
        var user = await _db.Users.SingleAsync();
        Assert.False((await _accounts.GetDeckPrivacyAsync(user.Id, default)).HideProfileDecks);
        await _accounts.SetDeckPrivacyAsync(user.Id, new(true), default);
        Assert.True((await _accounts.GetDeckPrivacyAsync(user.Id, default)).HideProfileDecks);
        await Assert.ThrowsAsync<AccountAuthenticationException>(() => _accounts.SetDeckPrivacyAsync(Guid.NewGuid(), new(false), default));
    }

    [Fact]
    public async Task RegistrationRequiresTermsAcceptance()
    {
        await Assert.ThrowsAsync<InvalidRequestException>(() => _accounts.RegisterAsync(new("Declined", "declined@example.test", "ValidPassword!123", false), "Test browser", default));
        Assert.False(await _db.Users.AnyAsync(u => u.Username == "Declined"));
    }

    private Task<AccountSignIn> Register() => _accounts.RegisterAsync(new("Nissa", "nissa@example.test", "ValidPassword!123", true), "Test browser", default);
    private ClaimsPrincipal Principal(Guid id, Guid? family = null, int version = 0) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim("ver", version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new Claim("session", family?.ToString() ?? "legacy"),
    ]));

    [Fact]
    public async Task Registration_keeps_the_existing_login_contract_and_hashes_refresh_tokens()
    {
        var session = await Register();
        Assert.Equal("Nissa", session.Response.Username);
        Assert.NotEmpty(session.Response.Token);
        var stored = await _db.AccountSessions.SingleAsync();
        Assert.DoesNotContain(session.RefreshToken, stored.RefreshHash);
        Assert.True(await _accounts.ValidateAsync(Principal(stored.UserId, stored.FamilyId), default));
        Assert.Equal("Test browser", (await _accounts.SessionsAsync(stored.UserId, stored.FamilyId, default)).Single().DeviceLabel);
    }

    [Fact]
    public async Task Refresh_is_single_use_and_replay_revokes_the_family()
    {
        var first = await Register();
        var second = await _accounts.RefreshAsync(first.RefreshToken, default);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        var session = await _db.AccountSessions.AsNoTracking().FirstAsync();
        await Assert.ThrowsAsync<AccountAuthenticationException>(() => _accounts.RefreshAsync(first.RefreshToken, default));
        Assert.False(await _accounts.ValidateAsync(Principal(session.UserId, session.FamilyId), default));
        await Assert.ThrowsAsync<AccountAuthenticationException>(() => _accounts.RefreshAsync(second.RefreshToken, default));
    }

    [Fact]
    public async Task Sessions_are_owned_and_sign_out_all_invalidates_legacy_tokens()
    {
        await Register();
        var session = await _db.AccountSessions.SingleAsync();
        await _accounts.RevokeAsync(Guid.NewGuid(), session.FamilyId, default);
        Assert.True(await _accounts.ValidateAsync(Principal(session.UserId, session.FamilyId), default));
        await _accounts.RevokeAllAsync(session.UserId, default);
        Assert.False(await _accounts.ValidateAsync(Principal(session.UserId, session.FamilyId), default));
        Assert.False(await _accounts.ValidateAsync(Principal(session.UserId), default));
    }

    [Fact]
    public async Task Verification_codes_expire_and_wrong_guesses_are_spent()
    {
        await Register();
        var user = await _db.Users.SingleAsync();
        var code = _mail.Code;
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<InvalidRequestException>(() => _accounts.VerifyAsync(user.Id, code == "000000" ? "111111" : "000000", default));
        await Assert.ThrowsAsync<InvalidRequestException>(() => _accounts.VerifyAsync(user.Id, code, default));
        _clock.Advance(TimeSpan.FromMinutes(2));
        await _accounts.ResendAsync(user.Id, default);
        _clock.Advance(TimeSpan.FromMinutes(11));
        await Assert.ThrowsAsync<InvalidRequestException>(() => _accounts.VerifyAsync(user.Id, _mail.Code, default));
    }

    [Fact]
    public async Task Verification_succeeds_and_resending_is_throttled()
    {
        await Register();
        var user = await _db.Users.SingleAsync();
        await _accounts.ResendAsync(user.Id, default);
        Assert.Equal(1, _mail.Count);
        await _accounts.VerifyAsync(user.Id, _mail.Code, default);
        Assert.True((await _accounts.GetAsync(user.Id, default)).EmailVerified);
    }

    [Fact]
    public async Task Reset_codes_are_single_use_and_revoke_existing_access()
    {
        await Register();
        var user = await _db.Users.SingleAsync();
        await _accounts.ForgotAsync("missing@example.test", default);
        Assert.Equal(1, _mail.Count);
        await _accounts.ForgotAsync(user.Email, default);
        var request = new ResetAccountPasswordRequest(user.Email, _mail.Code, "ReplacementPassword!123");
        await _accounts.ResetAsync(request, default);
        Assert.False(await _accounts.ValidateAsync(Principal(user.Id), default));
        await Assert.ThrowsAsync<InvalidRequestException>(() => _accounts.ResetAsync(request, default));
        var signedIn = await _accounts.LoginAsync(new("nissa@example.test", request.NewPassword), "New browser", default);
        Assert.Equal(user.Username, signedIn.Response.Username);
    }

    [Fact]
    public async Task Password_and_email_changes_require_current_password_and_reverify()
    {
        await Register();
        var user = await _db.Users.SingleAsync();
        await Assert.ThrowsAsync<InvalidRequestException>(() => _accounts.ChangePasswordAsync(user.Id, new("wrong", "ReplacementPassword!123"), default));
        await _accounts.VerifyAsync(user.Id, _mail.Code, default);
        await _accounts.ChangeEmailAsync(user.Id, new("ValidPassword!123", "new@example.test"), default);
        Assert.False((await _accounts.GetAsync(user.Id, default)).EmailVerified);
        Assert.Equal("new@example.test", user.Email);
        Assert.False(await _accounts.ValidateAsync(Principal(user.Id), default));
    }

    [Fact]
    public async Task Deletion_is_password_confirmed_and_hides_the_account()
    {
        await Register();
        var user = await _db.Users.SingleAsync();
        await Assert.ThrowsAsync<InvalidRequestException>(() => _accounts.DeleteAsync(user.Id, "wrong", default));
        await _accounts.DeleteAsync(user.Id, "ValidPassword!123", default);
        Assert.Empty(await _db.Users.ToArrayAsync());
        Assert.Empty(await _db.AccountSessions.ToArrayAsync());
        Assert.False(await _accounts.ValidateAsync(Principal(user.Id), default));
        Assert.EndsWith("@deleted.invalid", (await _db.Users.IgnoreQueryFilters().SingleAsync()).Email);
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
    private sealed class Mailbox : IAccountEmailSender
    {
        public string Code { get; private set; } = "";
        public int Count { get; private set; }
        public Task SendAsync(string address, string subject, string body, CancellationToken ct)
        {
            Count++;
            Code = Regex.Match(body, "[0-9]{6}", RegexOptions.None, TimeSpan.FromSeconds(1)).Value;
            return Task.CompletedTask;
        }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
