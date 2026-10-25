using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Controllers;
using MtgEngine.Api.Services;
using MtgEngine.Cards;

namespace MtgEngine.Api.Tests;

public sealed partial class GameHubTests
{
    [Fact]
    public async Task Four_player_snapshots_keep_each_hand_private_and_keep_all_public_seats()
    {
        var pool = Deck(ImplementationStatus.Verified);
        var store = new MemoryLogStore();
        var sessions = new GameSessionService(store, pool, new DeckGate(pool));
        var users = new[] { Alice, Bob, "user-three", "user-four" };
        var session = await sessions.CreateAsync(new NewGameRequest(
            [.. users.Select((user, seat) => new NewGameSeat(user, $"Seat {seat}", [.. DeckIds()], []))],
            42, false, "commander", 40));

        foreach (var (user, index) in users.Select((user, index) => (user, index)))
        {
            var controller = SnapshotController(sessions, user);
            var response = Assert.IsType<OkObjectResult>(await controller.View(session.Id, CancellationToken.None));
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var view = json.RootElement.GetProperty("view");
            Assert.Equal(index, view.GetProperty("viewer").GetProperty("value").GetInt32());
            Assert.Equal(4, view.GetProperty("players").GetArrayLength());
            foreach (var card in view.GetProperty("hand").EnumerateArray())
                Assert.Equal(index, card.GetProperty("owner").GetProperty("value").GetInt32());
            Assert.False(view.TryGetProperty("objects", out _));
            Assert.False(view.TryGetProperty("zones", out _));
            Assert.Equal(index, view.GetProperty("table").GetProperty("self").GetProperty("seat").GetInt32());
        }
    }

    [Fact]
    public async Task A_nonseated_account_cannot_read_a_game_snapshot()
    {
        var table = await TableAsync();
        Assert.IsType<NotFoundResult>(await SnapshotController(table.Sessions, "not-seated").View(table.Session.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await SnapshotController(table.Sessions, Alice).View(Guid.NewGuid(), CancellationToken.None));
    }

    private static GamesController SnapshotController(GameSessionService sessions, string user) =>
        new(null!, sessions)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test")),
                },
            },
        };
}
