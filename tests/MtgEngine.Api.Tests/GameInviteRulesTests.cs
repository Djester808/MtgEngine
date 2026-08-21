using System.ComponentModel.DataAnnotations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Getting to a game: who you can invite, and what an invitation has to say.
/// </summary>
/// <remarks>
/// Both of these are defects the lobby actually shipped with. The picker listed players from the
/// public community directory, which carries no user id on purpose, so every option bound to
/// nothing and no invitation could be sent at all — the page answered "one or more validation
/// errors occurred" and named no field. Underneath it, an invitation with the opponent left out
/// was accepted: <c>[Required]</c> cannot reject <c>Guid.Empty</c>, so it was addressed to
/// nobody and sat unanswerable until it expired.
/// </remarks>
public sealed class GameInviteRulesTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly MtgEngineDbContext _db;

    public GameInviteRulesTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(_conn).Options;
        _db = new MtgEngineDbContext(options);
        _db.Database.EnsureCreated();
    }

    private static IReadOnlyList<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, true);
        return results;
    }

    [Fact]
    public void An_invitation_with_no_opponent_is_refused()
    {
        // The shape the lobby was sending. It came back 200 and made an invitation addressed to
        // nobody, which is worse than a refusal: it looks like it worked.
        var request = new CreateInviteRequest { DeckId = Guid.NewGuid(), StartingLife = 40 };

        var errors = Validate(request);

        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(CreateInviteRequest.OpponentUserId)));
    }

    [Fact]
    public void An_invitation_with_no_deck_is_refused()
    {
        var request = new CreateInviteRequest { OpponentUserId = Guid.NewGuid() };

        var errors = Validate(request);

        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(CreateInviteRequest.DeckId)));
    }

    [Fact]
    public void An_invitation_naming_both_is_accepted()
    {
        var request = new CreateInviteRequest
        {
            DeckId = Guid.NewGuid(),
            OpponentUserId = Guid.NewGuid(),
            StartingLife = 40,
        };

        Assert.Empty(Validate(request));
    }

    [Fact]
    public void Accepting_without_a_deck_is_refused()
    {
        Assert.Contains(
            Validate(new AcceptInviteRequest()),
            e => e.MemberNames.Contains(nameof(AcceptInviteRequest.DeckId)));
    }

    [Fact]
    public async Task The_opponents_list_carries_an_id_to_invite()
    {
        // The whole point. A username is what the picker shows; an id is what an invitation
        // needs, and the list the lobby used to read had none.
        var me = Guid.NewGuid();
        _db.Users.AddRange(
            new User { Id = me, Username = "me", Email = "me@example.invalid", PasswordHash = "x" },
            new User { Id = Guid.NewGuid(), Username = "them", Email = "them@example.invalid", PasswordHash = "x" });
        await _db.SaveChangesAsync();

        var tables = new GameTableService(_db, null!, null!, null!);
        var opponents = await tables.OpponentsAsync(me);

        var them = Assert.Single(opponents);
        Assert.Equal("them", them.Username);
        Assert.NotEqual(Guid.Empty, them.UserId);
    }

    [Fact]
    public async Task You_are_not_on_your_own_list_of_opponents()
    {
        // CR 103.1 needs two players; inviting yourself is refused by the invite service, and
        // offering it in the picker would be offering a move the game will not make.
        var me = Guid.NewGuid();
        _db.Users.Add(new User { Id = me, Username = "me", Email = "me@example.invalid", PasswordHash = "x" });
        await _db.SaveChangesAsync();

        var tables = new GameTableService(_db, null!, null!, null!);

        Assert.Empty(await tables.OpponentsAsync(me));
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
