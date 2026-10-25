using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Hubs;
using MtgEngine.Api.Services;
using MtgEngine.Cards;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

// The deck-builder's card model and the engine's card definition share a name and mean
// different things; the engine's is the one a definition store hands out.
using CardDefinition = MtgEngine.Cards.CardDefinition;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The hub, actually invoked, over a real session and a real engine.
/// </summary>
/// <remarks>
/// The contract these exist for is the one that has been wrong before: the shape a client sends
/// and the shape the engine accepts. A structural test on each end passes while the two ends
/// disagree, so every test here goes in through <see cref="GameHub"/>'s own methods and reads
/// what came back out of the transport.
/// </remarks>
public sealed partial class GameHubTests
{
    private const string Alice = "user-alice";
    private const string Bob = "user-bob";

    /// <summary>A definition store with made-up cards, so nothing here depends on the corpus.</summary>
    private sealed class FakePool : ICardFilePool
    {
        private readonly Dictionary<string, (CardDefinition Definition, ImplementationStatus Status)> _cards = new(StringComparer.OrdinalIgnoreCase);

        public int Count => _cards.Count;

        public FakePool With(string oracleId, string name, ImplementationStatus status)
        {
            _cards[oracleId] = (
                new CardDefinition(oracleId, name, Layout.Normal,
                [
                    new Face(name, FaceRole.Main, new TypeLine(CardType.Creature, Supertype.None, ["Beast"]), "{1}{G}"),
                ]),
                status);
            return this;
        }

        public CardStatus StatusOf(string oracleId) =>
            _cards.TryGetValue(oracleId, out var found)
                ? new CardStatus(oracleId, found.Definition.Name, found.Status)
                : CardStatus.Unknown(oracleId);

        public CardDefinition? ByOracleId(string oracleId) =>
            _cards.TryGetValue(oracleId, out var found) ? found.Definition : null;

        public CardDefinition? ByName(string name) =>
            _cards.Values
                .Where(c => string.Equals(c.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Definition)
                .FirstOrDefault();
    }

    /// <summary>Group membership, which these tests do not assert on: nothing is ever sent to a group.</summary>
    private sealed class NoGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>The log store, in memory. One row per game, exactly as the database holds it.</summary>
    private sealed class MemoryLogStore : IGameLogStore
    {
        public Dictionary<Guid, PersistedGame> Rows { get; } = [];

        public int Saves { get; private set; }

        public Task<PersistedGame?> LoadAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Rows.TryGetValue(id, out var row) ? row : null);

        public Task CreateAsync(PersistedGame game, CancellationToken ct = default)
        {
            Rows[game.Id] = game;
            return Task.CompletedTask;
        }

        public Task<bool> TryCreateAsync(PersistedGame game, CancellationToken ct = default)
        {
            lock (Rows)
                return Task.FromResult(Rows.TryAdd(game.Id, game));
        }

        public Task SaveLogAsync(Guid id, string logJson, int eventCount, bool finished, CancellationToken ct = default)
        {
            Saves++;
            var row = Rows[id];
            row.LogJson = logJson;
            row.EventCount = eventCount;
            row.Finished = finished;
            return Task.CompletedTask;
        }
    }

    /// <summary>Records what the hub sent and to whom, instead of sending it.</summary>
    private sealed class RecordingClients : IHubCallerClients
    {
        public Func<string?, string, Task>? DelaySend { get; set; }
        public List<(string Reason, string? Rule)> Refusals { get; } = [];

        public List<string> ViewedBy { get; } = [];

        public List<string> ChoiceAskedOf { get; } = [];
        public List<PendingChoice> ChoicePayloads { get; } = [];

        public IClientProxy Caller => new Proxy(this, To.Caller, null);
        public IClientProxy Others => new Proxy(this, To.Other, null);
        public IClientProxy Client(string connectionId) => new Proxy(this, To.Other, null);
        public IClientProxy All => new Proxy(this, To.Other, null);
        public IClientProxy AllExcept(IReadOnlyList<string> excluded) => new Proxy(this, To.Other, null);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new Proxy(this, To.Other, null);
        public IClientProxy Group(string groupName) => new Proxy(this, To.Group, null);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new Proxy(this, To.Group, null);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded) => new Proxy(this, To.Group, null);
        public IClientProxy OthersInGroup(string groupName) => new Proxy(this, To.Group, null);
        public IClientProxy User(string userId) => new Proxy(this, To.User, userId);
        public IClientProxy Users(IReadOnlyList<string> userIds) => new Proxy(this, To.User, null);

        private enum To
        {
            Caller,
            Group,
            User,
            Other,
        }

        private sealed class Proxy(RecordingClients owner, To to, string? userId) : ISingleClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            {
                if (to == To.Caller && method == "Refused")
                    owner.Refusals.Add((args[0] as string ?? string.Empty, args[1] as string));

                if (to == To.User && userId is not null && method == "ViewUpdated")
                    owner.ViewedBy.Add(userId);

                if (to == To.User && userId is not null && method == "ChoiceRequested")
                {
                    owner.ChoiceAskedOf.Add(userId);
                    owner.ChoicePayloads.Add((PendingChoice)args[0]!);
                }

                // The rule a broadcast would break: nothing addressed to a group may carry state.
                if (to == To.Group)
                    throw new InvalidOperationException($"The hub broadcast {method} to a group.");

                return owner.DelaySend?.Invoke(userId, method) ?? Task.CompletedTask;
            }

            public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken ct = default) =>
                Task.FromResult<T>(default!);
        }
    }

    private sealed class FakeCaller(string userId) : HubCallerContext
    {
        public override string ConnectionId => "connection-1";

        public override string? UserIdentifier => userId;

        public override ClaimsPrincipal? User { get; } =
            new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }

    private sealed record Table(GameHub Hub, GameSessionService Sessions, GameSession Session, RecordingClients Clients, MemoryLogStore Store);

    /// <summary>A two-seat game, created through the real service, with the transport recorded.</summary>
    private static async Task<Table> TableAsync(
        string actingAs = Alice,
        bool allowHonourSystem = true,
        ImplementationStatus status = ImplementationStatus.Translated,
        MemoryLogStore? store = null,
        FakePool? pool = null)
    {
        pool ??= Deck(status);
        store ??= new MemoryLogStore();
        var sessions = new GameSessionService(store, pool, new DeckGate(pool));
        var session = await sessions.CreateAsync(new NewGameRequest(
            [
                new NewGameSeat(Alice, "Alice", [.. DeckIds()], []),
                new NewGameSeat(Bob, "Bob", [.. DeckIds()], []),
            ],
            Seed: 42,
            allowHonourSystem,
            Format: "commander",
            StartingLife: 20));

        // These transport tests exercise in-game actions; complete the real opening procedure.
        while (session.State.Pending is { Kind: "mulligan" } opening)
            await sessions.ApplyAsync(session, new Rules.Commands.AnswerChoice(opening.Player, opening.Id, ["keep"]));

        return Hub(sessions, session, store, actingAs);
    }

    private static Table Hub(GameSessionService sessions, GameSession session, MemoryLogStore store, string actingAs)
    {
        var clients = new RecordingClients();
        return new Table(
            new GameHub(sessions, NullLogger<GameHub>.Instance)
            {
                Context = new FakeCaller(actingAs),
                Clients = clients,
                Groups = new NoGroups(),
            },
            sessions,
            session,
            clients,
            store);
    }

    private static FakePool Deck(ImplementationStatus status)
    {
        var pool = new FakePool();
        for (var i = 0; i < 12; i++)
            pool.With($"o-card-{i}", $"Test Card {i}", status);
        return pool;
    }

    private static List<string> DeckIds() => [.. Enumerable.Range(0, 12).Select(i => $"o-card-{i}")];

    [Fact]
    public async Task Joining_sends_the_caller_their_own_view()
    {
        var table = await TableAsync();

        await table.Hub.Join(table.Session.Id);

        Assert.Equal([Alice], table.Clients.ViewedBy);
    }

    [Fact]
    public async Task A_caller_with_no_seat_at_the_table_is_turned_away()
    {
        // The whole attribution rule in one assertion: the seat is the token's, and an account
        // that never sat down cannot look at the game, let alone act in it.
        var table = await TableAsync(actingAs: "user-stranger");

        var error = await Assert.ThrowsAsync<HubException>(() => table.Hub.Join(table.Session.Id));

        Assert.Contains("not seated", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(table.Clients.ViewedBy);
    }

    [Fact]
    public async Task An_applied_command_pushes_a_view_to_every_seat()
    {
        var table = await TableAsync();

        await table.Hub.Command(table.Session.Id, new ConcedeDto());

        Assert.Equal([Alice, Bob], table.Clients.ViewedBy);
        Assert.Empty(table.Clients.Refusals);
    }

    [Fact]
    public async Task A_slow_recipient_does_not_delay_other_seats_views()
    {
        var table = await TableAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        table.Clients.DelaySend = (user, method) =>
            user == Alice && method == "ViewUpdated" ? release.Task : Task.CompletedTask;
        var command = table.Hub.Command(table.Session.Id, new ConcedeDto());
        try
        {
            Assert.False(command.IsCompleted);
            Assert.Contains(Bob, table.Clients.ViewedBy);
        }
        finally
        {
            release.SetResult();
            await command;
        }
    }

    [Fact]
    public async Task A_command_the_rules_refuse_reaches_only_the_caller_and_names_the_rule()
    {
        var table = await TableAsync();
        await table.Hub.Command(table.Session.Id, new ConcedeDto());
        table.Clients.ViewedBy.Clear();

        // CR 104.1: the game is over, so nothing else can be done at this table.
        await table.Hub.Command(table.Session.Id, new PassPriorityDto());

        var (reason, rule) = Assert.Single(table.Clients.Refusals);
        Assert.Equal("104.1", rule);
        Assert.NotEmpty(reason);
        Assert.Empty(table.Clients.ViewedBy);
    }

    [Fact]
    public async Task An_applied_command_is_appended_to_the_stored_log()
    {
        var table = await TableAsync();
        var before = table.Store.Rows[table.Session.Id].EventCount;

        await table.Hub.Command(table.Session.Id, new ConcedeDto());

        var row = table.Store.Rows[table.Session.Id];
        Assert.True(row.EventCount > before);
        Assert.True(row.Finished);
        Assert.Equal(row.EventCount, EventLogSerializer.Read(row.LogJson).Count);
    }

    [Fact]
    public async Task A_game_comes_back_from_its_log_after_a_restart()
    {
        // Nothing but the log is stored, so this is the only proof the table survives a restart.
        var store = new MemoryLogStore();
        var pool = Deck(ImplementationStatus.Translated);
        var table = await TableAsync(store: store, pool: pool);
        await table.Hub.Command(table.Session.Id, new ConcedeDto());

        var restarted = new GameSessionService(store, pool, new DeckGate(pool));
        var resumed = await restarted.FindAsync(table.Session.Id);

        Assert.NotNull(resumed);
        Assert.Equal(table.Session.Log.Count, resumed.Log.Count);
        Assert.True(resumed.Finished);
        Assert.Equal(new PlayerId(1), resumed.SeatOf(Bob)!.Value);
    }

    [Fact]
    public async Task A_pending_choice_is_asked_of_the_player_it_belongs_to_and_nobody_else()
    {
        var store = new MemoryLogStore();
        var pool = Deck(ImplementationStatus.Translated);
        var table = await TableAsync(store: store, pool: pool);

        // The choice systems are a wave-2 seam, so the question is put into the log directly:
        // what is under test is who the hub tells about it, not who asked.
        var log = EventLogSerializer.Read(store.Rows[table.Session.Id].LogJson).ToList();
        log.Add(new ChoiceRequested(new PendingChoice(
            new ChoiceId(1), new PlayerId(1), "mulligan", "Keep this hand?",
            [new ChoiceOption("keep", "Keep")], 1, 1,
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty))));
        store.Rows[table.Session.Id].LogJson = EventLogSerializer.Write(log);

        var restarted = new GameSessionService(store, pool, new DeckGate(pool));
        var resumed = await restarted.FindAsync(table.Session.Id);
        var bobs = Hub(restarted, resumed!, store, Bob);
        var alices = Hub(restarted, resumed!, store, Alice);

        await bobs.Hub.Join(resumed!.Id);
        await alices.Hub.Join(resumed.Id);

        Assert.Equal([Bob], bobs.Clients.ChoiceAskedOf);
        Assert.Empty(alices.Clients.ChoiceAskedOf);
        Assert.Equal([Alice], alices.Clients.ViewedBy);
    }

    [Fact]
    public async Task A_manual_action_is_refused_where_the_table_enforces_every_card()
    {
        var table = await TableAsync(allowHonourSystem: false, status: ImplementationStatus.Verified);

        await table.Hub.Command(table.Session.Id, new ManualLifeDto(0, -1));

        var (reason, _) = Assert.Single(table.Clients.Refusals);
        Assert.Contains("enforces every card", reason, StringComparison.Ordinal);
        Assert.Empty(table.Clients.ViewedBy);
    }

    [Fact]
    public async Task A_manual_action_on_a_card_the_engine_plays_is_refused()
    {
        // The honour-system line: the table admits unenforced cards, and this one is not.
        var table = await TableAsync(allowHonourSystem: true, status: ImplementationStatus.Verified);
        var enforced = table.Session.State.Objects.Keys.First();

        await table.Hub.Command(table.Session.Id, new ManualTapDto(enforced.Value, Tapped: true));

        var (reason, _) = Assert.Single(table.Clients.Refusals);
        Assert.Contains("cannot be handled manually", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manual_action_on_a_card_the_engine_does_not_play_is_applied()
    {
        var table = await TableAsync(allowHonourSystem: true, status: ImplementationStatus.Translated);

        await table.Hub.Command(table.Session.Id, new ManualLifeDto(0, -3));

        Assert.Empty(table.Clients.Refusals);
        Assert.Equal(17, table.Session.State.Players[0].Life);
        Assert.Equal([Alice, Bob], table.Clients.ViewedBy);
    }

    [Fact]
    public async Task A_malformed_payload_is_refused_rather_than_applied()
    {
        var table = await TableAsync();

        // The DTO's own caps, which no MVC pipeline runs for a hub.
        await table.Hub.Command(table.Session.Id, new ManualCountersDto(1, "+1/+1", 100_000));

        Assert.Single(table.Clients.Refusals);
        Assert.Empty(table.Clients.ViewedBy);
    }

    [Fact]
    public void No_command_payload_can_carry_a_player_id()
    {
        // The rule the previous hub kept by convention and this one keeps structurally: a client
        // that could name its own player could act as its opponent. The seat is the token's.
        var payloads = typeof(GameCommandDto).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(GameCommandDto)))
            .ToList();

        Assert.NotEmpty(payloads);
        foreach (var payload in payloads)
        {
            var named = payload.GetProperties()
                .Where(p => p.Name is "Player" or "PlayerId" or "UserId" or "Actor" or "Me")
                .ToList();
            Assert.True(named.Count == 0, $"{payload.Name} carries {string.Join(", ", named.Select(p => p.Name))}.");
        }
    }

    [Fact]
    public void Activation_wire_payload_preserves_selected_objects()
    {
        GameCommandDto dto = new ActivateAbilityDto(3, 0, null, null, null, null, null)
        { CostObjects = [4, 5] };
        var read = JsonSerializer.Deserialize<GameCommandDto>(JsonSerializer.Serialize(dto))!;
        var command = Assert.IsType<MtgEngine.Rules.Commands.ActivateAbility>(read.ToCommand(new PlayerId(1)));
        Assert.Equal(new[] { new ObjectId(4), new ObjectId(5) }, command.CostObjects);
        Assert.Equal(new PlayerId(1), command.Player);
    }

    [Fact]
    public void Every_command_kind_round_trips_through_the_wire_shape()
    {
        // A payload the client can send but the server cannot read is the drift this hierarchy
        // exists to prevent, and it is invisible until somebody plays that card.
        GameCommandDto[] commands =
        [
            new PassPriorityDto(),
            new ConcedeDto(),
            new PlayLandDto(3),
            new CastSpellDto(3, 0, [4], [1], [0], 2, ["kicker"]),
            new ActivateAbilityDto(3, 1, null, null, null, null, null) { CostObjects = [4] },
            new DeclareAttackersDto([new AttackDto(3, 1, null)]),
            new DeclareBlockersDto([new BlockDto(4, 3)]),
            new AnswerChoiceDto(1, ["keep"]),
            new ManualMoveDto(3, new ZoneRefDto("Battlefield", null), -1, false),
            new ManualTapDto(3, true),
            new ManualCountersDto(3, "+1/+1", 1),
            new ManualLifeDto(1, -2),
        ];

        foreach (var command in commands)
        {
            var json = JsonSerializer.Serialize(command, typeof(GameCommandDto));
            var back = JsonSerializer.Deserialize<GameCommandDto>(json);

            Assert.NotNull(back);
            Assert.Equal(command.GetType(), back.GetType());
            // Record equality is reference equality for the list members, so the wire shape is
            // what is compared: what went out is what a second pass writes back.
            Assert.Equal(json, JsonSerializer.Serialize(back, typeof(GameCommandDto)));
            Assert.NotNull(back.ToCommand(new PlayerId(0)));
            Assert.Contains("\"kind\"", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_deck_of_unverified_cards_cannot_sit_at_a_table_that_enforces_them()
    {
        var pool = Deck(ImplementationStatus.Translated);
        var sessions = new GameSessionService(new MemoryLogStore(), pool, new DeckGate(pool));

        await Assert.ThrowsAsync<InvalidRequestException>(() => sessions.CreateAsync(new NewGameRequest(
            [
                new NewGameSeat(Alice, "Alice", [.. DeckIds()], []),
                new NewGameSeat(Bob, "Bob", [.. DeckIds()], []),
            ],
            Seed: 1,
            AllowHonourSystem: false,
            Format: "commander",
            StartingLife: 20)));
    }
}
