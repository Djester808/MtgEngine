using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Recording games from the life counter.
/// </summary>
/// <remarks>
/// The tests that matter here are the attribution ones. A life counter runs on one shared
/// device, so the request naming who sat where is a claim by whoever holds the phone — and if
/// that claim were believed, anyone could write losses onto a stranger's record. Every case
/// below that ends in "guest" or an exception is that rule being enforced.
/// </remarks>
public sealed class LifeMatchServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly MtgEngineDbContext _db;
    private readonly TokenService _tokens;
    private readonly LifeMatchService _sut;

    private readonly User _nissa;
    private readonly User _jace;

    public LifeMatchServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(_conn).Options;
        _db = new MtgEngineDbContext(options);
        _db.Database.EnsureCreated();

        _nissa = AddUser("Nissa");
        _jace = AddUser("Jace");
        _db.SaveChanges();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "life-counter-tests-signing-key-long-enough-for-hmac-sha256",
            })
            .Build();

        _tokens = new TokenService(config);
        _sut = new LifeMatchService(_db, _tokens);
    }

    private User AddUser(string username)
    {
        var user = new User { Username = username, Email = $"{username}@example.com", PasswordHash = "x" };
        _db.Users.Add(user);
        return user;
    }

    private static RecordMatchSeatRequest Seat(
        int seat,
        string name,
        string? token = null,
        bool won = false,
        string? reason = null,
        int finalLife = 0) =>
        new()
        {
            Seat = seat,
            DisplayName = name,
            Token = token,
            Won = won,
            LossReason = reason,
            FinalLife = finalLife,
        };

    private Task<MatchRecordedDto> Record(params RecordMatchSeatRequest[] seats) =>
        _sut.RecordAsync(
            _nissa.Id,
            new RecordMatchRequest { StartingLife = 40, Seats = seats },
            CancellationToken.None);

    // ---- Attribution -------------------------------------------------------------------

    [Fact]
    public async Task Seat_with_a_valid_token_is_tied_to_that_account()
    {
        await Record(
            Seat(0, "Nissa", _tokens.Generate(_nissa), won: true, finalLife: 12),
            Seat(1, "Jace", _tokens.Generate(_jace), reason: "LifeTotal", finalLife: -3));

        var seats = await _db.LifeMatchSeats.OrderBy(s => s.Seat).ToListAsync();

        Assert.Equal(_nissa.Id, seats[0].UserId);
        Assert.Equal(_jace.Id, seats[1].UserId);
        Assert.True(seats[0].Won);
        Assert.Equal(MatchLossReason.LifeTotal, seats[1].LossReason);
    }

    [Fact]
    public async Task Seat_without_a_token_is_a_guest_and_lands_on_nobody_record()
    {
        var result = await Record(
            Seat(0, "Nissa", _tokens.Generate(_nissa), won: true),
            Seat(1, "Someone from the shop"));

        var guest = await _db.LifeMatchSeats.SingleAsync(s => s.Seat == 1);

        Assert.Null(guest.UserId);
        Assert.Equal("Someone from the shop", guest.DisplayName);
        Assert.Equal(["Nissa"], result.AttributedUsernames);
    }

    [Fact]
    public async Task A_seat_cannot_be_claimed_for_an_account_without_that_account_token()
    {
        // The request names Jace in seat 1 and supplies nothing to back it up. The row must
        // record the name and no user id — otherwise every account's record is writable by
        // anyone who knows their display name.
        await Record(Seat(0, "Nissa", _tokens.Generate(_nissa), won: true), Seat(1, "Jace"));

        Assert.Empty(await _db.LifeMatchSeats.Where(s => s.UserId == _jace.Id).ToListAsync());
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_refused()
    {
        var forged = new TokenService(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Secret"] = "a-different-key-entirely-but-also-long-enough-to-sign",
                })
                .Build())
            .Generate(_jace);

        var ex = await Assert.ThrowsAsync<InvalidRequestException>(() =>
            Record(Seat(0, "Nissa", _tokens.Generate(_nissa), won: true), Seat(1, "Jace", forged)));

        Assert.Contains("seat 2", ex.Message);
        Assert.Empty(await _db.LifeMatches.ToListAsync());
    }

    [Fact]
    public async Task A_token_whose_account_is_gone_is_reported_rather_than_downgraded_to_a_guest()
    {
        // A JWT stays cryptographically valid after the account behind it is deleted, so
        // signature checking alone would happily write a seat against a user id nothing can
        // read back. Failing loudly is also the general rule here: a player who watched
        // themselves sign in and then win must not find the game silently filed under nobody.
        var orphaned = _tokens.Generate(_jace);

        _db.Users.Remove(_jace);
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidRequestException>(() =>
            Record(Seat(0, "Nissa", _tokens.Generate(_nissa), won: true), Seat(1, "Jace", orphaned)));

        Assert.Contains("no longer exists", ex.Message);
    }

    [Fact]
    public void An_unsigned_token_is_not_taken_on_its_own_word()
    {
        // "alg": "none" with the same NameIdentifier claim. Nothing in the app produces one;
        // the check exists because a validator that did not pin the algorithm would accept it.
        const string unsigned =
            "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0."
            + "eyJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1laWRlbnRpZmllciI6ImIxN2ZmYzJkLTAwMDAtNDAwMC04MDAwLTAwMDAwMDAwMDAwMSJ9.";

        Assert.False(_tokens.TryReadUserId(unsigned, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    public void Junk_is_not_a_sign_in(string? token)
    {
        Assert.False(_tokens.TryReadUserId(token, out _));
    }

    [Fact]
    public async Task One_account_cannot_hold_two_seats()
    {
        var token = _tokens.Generate(_nissa);

        await Assert.ThrowsAsync<InvalidRequestException>(() =>
            Record(Seat(0, "Nissa", token, won: true), Seat(1, "Nissa again", token)));
    }

    // ---- Shape of a game ----------------------------------------------------------------

    [Fact]
    public async Task Two_winners_is_refused()
    {
        await Assert.ThrowsAsync<InvalidRequestException>(() =>
            Record(Seat(0, "Nissa", won: true), Seat(1, "Jace", won: true)));
    }

    [Fact]
    public async Task Duplicate_seat_numbers_are_refused()
    {
        await Assert.ThrowsAsync<InvalidRequestException>(() =>
            Record(Seat(0, "Nissa", won: true), Seat(0, "Jace")));
    }

    [Fact]
    public async Task Eight_seats_are_stored()
    {
        var seats = Enumerable.Range(0, 8)
            .Select(i => Seat(i, $"Player {i + 1}", won: i == 0))
            .ToArray();

        await Record(seats);

        Assert.Equal(8, await _db.LifeMatchSeats.CountAsync());
    }

    [Fact]
    public async Task An_unknown_loss_reason_becomes_unspecified_rather_than_a_nameless_value()
    {
        await Record(Seat(0, "Nissa", won: true), Seat(1, "Jace", reason: "42"));

        var loser = await _db.LifeMatchSeats.SingleAsync(s => s.Seat == 1);
        Assert.Equal(MatchLossReason.Unspecified, loser.LossReason);
    }

    [Theory]
    [InlineData("Poison", MatchLossReason.Poison)]
    [InlineData("commanderdamage", MatchLossReason.CommanderDamage)]
    [InlineData("Conceded", MatchLossReason.Conceded)]
    [InlineData("EmptyLibrary", MatchLossReason.EmptyLibrary)]
    public async Task Every_loss_reason_the_counter_can_report_round_trips(string sent, MatchLossReason stored)
    {
        await Record(Seat(0, "Nissa", won: true), Seat(1, "Jace", reason: sent));

        var loser = await _db.LifeMatchSeats.SingleAsync(s => s.Seat == 1);
        Assert.Equal(stored, loser.LossReason);
    }

    [Fact]
    public async Task A_start_time_from_the_future_is_replaced_with_now()
    {
        var before = DateTime.UtcNow;

        await _sut.RecordAsync(
            _nissa.Id,
            new RecordMatchRequest
            {
                StartingLife = 40,
                StartedAt = DateTime.UtcNow.AddYears(3),
                Seats = [Seat(0, "Nissa", won: true), Seat(1, "Jace")],
            },
            CancellationToken.None);

        var match = await _db.LifeMatches.SingleAsync();
        Assert.InRange(match.StartedAt, before, DateTime.UtcNow.AddSeconds(1));
    }

    // ---- Reading a record ----------------------------------------------------------------

    [Fact]
    public async Task A_record_counts_only_the_seats_that_belong_to_the_player()
    {
        var nissaToken = _tokens.Generate(_nissa);
        var jaceToken = _tokens.Generate(_jace);

        await Record(Seat(0, "Nissa", nissaToken, won: true), Seat(1, "Jace", jaceToken));
        await Record(Seat(0, "Nissa", nissaToken), Seat(1, "Jace", jaceToken, won: true));
        await Record(Seat(0, "Nissa", nissaToken), Seat(1, "Jace", jaceToken, won: true));
        // A game Nissa was not in at all.
        await Record(Seat(0, "Jace", jaceToken, won: true), Seat(1, "A guest"));

        var record = await _sut.GetRecordAsync(_nissa.Id, CancellationToken.None);

        Assert.Equal(3, record.Played);
        Assert.Equal(1, record.Wins);
        Assert.Equal(2, record.Losses);
        Assert.Equal(0.333, record.WinRate);
    }

    [Fact]
    public async Task An_empty_record_does_not_divide_by_zero()
    {
        var record = await _sut.GetRecordAsync(_nissa.Id, CancellationToken.None);

        Assert.Equal(0, record.Played);
        Assert.Equal(0, record.WinRate);
        Assert.Empty(record.Recent);
    }

    [Fact]
    public async Task Recent_games_carry_the_other_seats_names()
    {
        await Record(
            Seat(0, "Nissa", _tokens.Generate(_nissa), reason: "CommanderDamage", finalLife: 8),
            Seat(1, "Jace", won: true),
            Seat(2, "Chandra"));

        var recent = Assert.Single((await _sut.GetRecordAsync(_nissa.Id, CancellationToken.None)).Recent);

        Assert.Equal(3, recent.SeatCount);
        Assert.False(recent.Won);
        Assert.Equal("CommanderDamage", recent.LossReason);
        Assert.Equal(8, recent.FinalLife);
        Assert.Equal(["Jace", "Chandra"], recent.Opponents);
    }

    [Fact]
    public async Task Played_counts_every_game_even_past_the_recent_page()
    {
        var token = _tokens.Generate(_nissa);
        for (var i = 0; i < LifeMatchLimits.RecentMatches + 5; i++)
            await Record(Seat(0, "Nissa", token, won: true), Seat(1, "A guest"));

        var record = await _sut.GetRecordAsync(_nissa.Id, CancellationToken.None);

        Assert.Equal(LifeMatchLimits.RecentMatches + 5, record.Played);
        Assert.Equal(LifeMatchLimits.RecentMatches, record.Recent.Length);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
