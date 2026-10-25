using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Tests;

public sealed partial class GameHubTests
{
    [Fact]
    public void Removed_payment_order_is_rejected_instead_of_silently_ignored()
    {
        const string json = """
            {"kind":"activate","Source":3,"AbilityIndex":0,"PaymentOrder":[1,0]}
            """;
        Assert.NotNull(JsonSerializer.Deserialize<GameCommandDto>(json.Replace(",\"PaymentOrder\":[1,0]", "", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GameCommandDto>(json));
    }

    [Fact]
    public async Task Removed_recovery_logs_have_a_controlled_http_and_hub_error()
    {
        var store = new MemoryLogStore();
        var pool = Deck(ImplementationStatus.Translated);
        var table = await TableAsync(store: store, pool: pool);
        var row = store.Rows[table.Session.Id];
        var log = EventLogSerializer.Read(row.LogJson).ToList();
        log.Add(new MtgEngine.Rules.Casting.PaymentRecoveryApplied());
        row.LogJson = EventLogSerializer.Write(log);
        var persisted = row.LogJson;
        var restarted = new GameSessionService(store, pool, new DeckGate(pool));

        var failure = await Assert.ThrowsAsync<UnsupportedGameLogException>(() => restarted.FindAsync(table.Session.Id));
        var http = new DefaultHttpContext();
        await using var body = new MemoryStream();
        http.Response.Body = body;
        var handler = new AiExceptionHandler(NullLogger<AiExceptionHandler>.Instance);
        Assert.True(await handler.TryHandleAsync(http, failure, CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);
        body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(body);
        Assert.Equal("Unsupported saved game", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal("This saved game uses removed payment recovery. Start a new game.", problem.RootElement.GetProperty("detail").GetString());

        var hub = Hub(restarted, table.Session, store, Alice).Hub;
        var error = await Assert.ThrowsAsync<HubException>(() => hub.Join(table.Session.Id));
        Assert.Equal("This saved game uses removed payment recovery. Start a new game.", error.Message);
        Assert.Equal(persisted, row.LogJson);
    }
}
