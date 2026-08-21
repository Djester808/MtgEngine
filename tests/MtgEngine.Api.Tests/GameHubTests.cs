using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Hubs;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The hub, actually invoked.
/// </summary>
/// <remarks>
/// Until now the hub was only checked structurally — that nothing on it can hand out a
/// <c>GameState</c> — and never called. That left the contract between the client and the engine
/// resting on two tests that each looked at one end of it: a client spec asserting the shape it
/// sends, and an engine test asserting the shape it accepts. Nothing checked that they were the
/// same shape, and a wire nobody has run is a wire nobody knows the state of.
/// </remarks>
public sealed class GameHubTests
{
    /// <summary>
    /// A card lookup with nothing in it.
    /// </summary>
    /// <remarks>
    /// The hub decorates the views it pushes with card art, which is a screen concern and
    /// nothing to do with what these tests assert. An empty lookup leaves the view exactly as
    /// the engine projected it.
    /// </remarks>
    private static GameCardArt NoArt => new(new NoCards());

    private sealed class NoCards : ICardLookup
    {
        public Task<CardDefinition?> GetByOracleIdAsync(string oracleId) =>
            Task.FromResult<CardDefinition?>(null);

        public Task<CardDefinition?> GetByNameAsync(string name) =>
            Task.FromResult<CardDefinition?>(null);

        public Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId) =>
            Task.FromResult<CardDefinition?>(null);

        public Task<PrintingDto[]> GetPrintingsAsync(string oracleId) => Task.FromResult<PrintingDto[]>([]);

        public Task<RulingDto[]> GetRulingsAsync(string oracleId) => Task.FromResult<RulingDto[]>([]);

        public Task<CardDefinition[]> SearchAsync(
            string query, int limit = 20, int offset = 0, string sortBy = "name",
            string sortDir = "asc", bool matchCase = false, bool matchWord = false,
            bool useRegex = false) => Task.FromResult<CardDefinition[]>([]);
    }

    private static CardDefinition Card(string name) => new()
    {
        OracleId = "oracle-" + name.ToLowerInvariant(),
        Name = name,
        CardTypes = CardType.Creature,
        Power = 2,
        Toughness = 2,
    };

    private static CardDefinition Walker() => new()
    {
        OracleId = "oracle-chandra",
        Name = "Chandra, Torch of Defiance",
        CardTypes = CardType.Planeswalker,
        Supertypes = ["Legendary"],
        Subtypes = ["Chandra"],
        StartingLoyalty = 4,
    };

    /// <summary>A hub wired to a real session, with the transport recorded rather than sent.</summary>
    private sealed class Harness
    {
        public required GameHub Hub { get; init; }
        public required GameSessionService Sessions { get; init; }
        public required Guid GameId { get; init; }
        public required Guid Alice { get; init; }
        public required Guid Bob { get; init; }
        public required RecordingClients Clients { get; init; }
    }

    private static async Task<Harness> TableAsync(Guid? actingAs = null)
    {
        var sessions = new GameSessionService(NoAbilities.Instance, new InMemoryGameStore());
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var gameId = await sessions.CreateAsync(
        [
            new PlayerSetup(alice, "Alice", 20, [.. Enumerable.Range(1, 30).Select(i => Card($"A{i}"))]),
            new PlayerSetup(bob, "Bob", 20, [.. Enumerable.Range(1, 30).Select(i => Card($"B{i}"))]),
        ],
            seed: 42);

        var clients = new RecordingClients();
        var hub = new GameHub(sessions, NoArt, NullLogger<GameHub>.Instance)
        {
            Context = new FakeCaller(actingAs ?? alice),
            Clients = clients,
        };

        return new Harness
        {
            Hub = hub,
            Sessions = sessions,
            GameId = gameId,
            Alice = alice,
            Bob = bob,
            Clients = clients,
        };
    }

    /// <summary>
    /// Puts a creature and an opposing planeswalker on the board and walks to the step where
    /// attackers are declared, on a turn where that creature can actually attack.
    /// </summary>
    /// <remarks>
    /// The loop waits for Alice to be the active player <em>and</em> the creature to have lost
    /// summoning sickness (CR 302.6), rather than stopping at the first declare-attackers step:
    /// who goes first is decided at random (CR 103.1), and a test that assumed it would be
    /// stopping in Bob's combat about half the time.
    /// </remarks>
    private static async Task<(ObjectId Attacker, ObjectId Walker)> ToCombatAsync(Harness table)
    {
        return await table.Sessions.Find(table.GameId)!.MutateAsync(game =>
        {
            for (var guard = 0; guard < 20 && game.State.Choice is { } choice; guard++)
                game.Choose(choice.PlayerId, ["keep"]);

            var attacker = game.Create(table.Alice, Card("Bear"), Zone.Battlefield);
            var walker = game.Create(table.Bob, Walker(), Zone.Battlefield);

            for (var guard = 0; guard < 400; guard++)
            {
                var ready =
                    game.State.CurrentStep == TurnStep.DeclareAttackers
                    && game.State.ActivePlayerId == table.Alice
                    && game.State.GetObject(attacker).Permanent?.HasSummoningSickness == false;

                if (ready)
                    break;

                if (game.State.Choice is { } choice)
                    game.Choose(choice.PlayerId, [choice.Options[0].Id]);
                else if (game.State.Priority.Holder is { } holder)
                    game.PassPriority(holder);
                else
                    break;
            }

            Assert.Equal(TurnStep.DeclareAttackers, game.State.CurrentStep);
            Assert.Equal(table.Alice, game.State.ActivePlayerId);

            return (attacker, walker);
        });
    }

    [Fact]
    public async Task An_attack_named_by_a_client_reaches_the_engine_as_an_attack_on_that_planeswalker()
    {
        // The contract this test exists for: AttackDto's optional Planeswalker becomes the
        // engine's AttackTarget (CR 508.1b). The client sends this shape and the engine accepts
        // that one; nothing until now checked they were the same shape.
        var table = await TableAsync();
        var (attacker, walker) = await ToCombatAsync(table);

        await table.Hub.DeclareAttackers(
            table.GameId,
            new Dictionary<Guid, AttackDto>
            {
                [attacker.Value] = new(table.Bob, walker.Value),
            });

        var combat = await table.Sessions.Find(table.GameId)!.MutateAsync(game => game.State.Combat);

        Assert.True(combat.Attackers.ContainsKey(attacker));
        Assert.True(combat.Attackers[attacker].IsPlaneswalker);
        Assert.Equal(walker, combat.Attackers[attacker].Planeswalker);
        Assert.Empty(table.Clients.Refusals);
    }

    [Fact]
    public async Task An_attack_with_no_planeswalker_named_goes_at_the_player()
    {
        // The common case, and the one the client sends as a null: it must not be read as
        // "a planeswalker whose id happens to be empty".
        var table = await TableAsync();
        var (attacker, walker) = await ToCombatAsync(table);

        await table.Hub.DeclareAttackers(
            table.GameId,
            new Dictionary<Guid, AttackDto> { [attacker.Value] = new(table.Bob, null) });

        var combat = await table.Sessions.Find(table.GameId)!.MutateAsync(game => game.State.Combat);

        Assert.False(combat.Attackers[attacker].IsPlaneswalker);
        Assert.Equal(table.Bob, combat.Attackers[attacker].DefendingPlayer);
    }

    [Fact]
    public async Task A_refused_attack_changes_nothing_and_is_told_only_to_the_caller()
    {
        // Attacking a planeswalker its controller does not control is refused (CR 508.1b). The
        // player who tried it hears why; nobody else hears anything, because a refusal is not a
        // move and the other player has nothing to redraw.
        var table = await TableAsync();
        var (attacker, walker) = await ToCombatAsync(table);

        await table.Hub.DeclareAttackers(
            table.GameId,
            new Dictionary<Guid, AttackDto>
            {
                // Alice's own planeswalker id, named as though Bob controlled it.
                [attacker.Value] = new(table.Bob, ObjectId.New().Value),
            });

        var combat = await table.Sessions.Find(table.GameId)!.MutateAsync(game => game.State.Combat);

        Assert.Empty(combat.Attackers);
        Assert.Single(table.Clients.Refusals);
        Assert.Empty(table.Clients.GroupSends);
    }

    [Fact]
    public async Task A_blocker_named_by_a_client_reaches_the_engine()
    {
        // The other half of combat, and the half the browser harness could not pin down: it
        // could never reliably occupy the declare-blockers window before something moved the
        // game on. The board's own logic is covered by a component spec; this covers the wire.
        var table = await TableAsync();
        var (attacker, _) = await ToCombatAsync(table);

        // Alice attacks; Bob is the one who blocks (CR 509.1).
        await table.Hub.DeclareAttackers(
            table.GameId,
            new Dictionary<Guid, AttackDto> { [attacker.Value] = new(table.Bob, null) });

        var blocker = await table.Sessions.Find(table.GameId)!.MutateAsync(game =>
        {
            var id = game.Create(table.Bob, Card("Wall"), Zone.Battlefield);

            // Straight to the blockers step: the declaration is a turn-based action taken
            // before anyone has priority (CR 509.1), so there is nothing to pass here.
            for (var guard = 0; guard < 50 && game.State.CurrentStep != TurnStep.DeclareBlockers; guard++)
            {
                if (game.State.Priority.Holder is { } holder)
                    game.PassPriority(holder);
                else
                    break;
            }

            return id;
        });

        var bobsHub = new GameHub(table.Sessions, NoArt, NullLogger<GameHub>.Instance)
        {
            Context = new FakeCaller(table.Bob),
            Clients = table.Clients,
        };

        await bobsHub.DeclareBlockers(
            table.GameId,
            new Dictionary<Guid, Guid[]> { [attacker.Value] = [blocker.Value] });

        var combat = await table.Sessions.Find(table.GameId)!.MutateAsync(game => game.State.Combat);

        Assert.True(combat.BlockersDeclared);
        Assert.Contains(blocker, combat.BlockersOf(attacker));
        Assert.Contains(attacker, combat.Blocked);
        Assert.Empty(table.Clients.Refusals);
    }

    [Fact]
    public async Task Only_the_defending_player_declares_blockers()
    {
        // CR 509.1. The attacking player naming their opponent's blocks would be choosing how
        // their own attack is answered.
        var table = await TableAsync();
        var (attacker, _) = await ToCombatAsync(table);

        await table.Hub.DeclareAttackers(
            table.GameId,
            new Dictionary<Guid, AttackDto> { [attacker.Value] = new(table.Bob, null) });

        var blocker = await table.Sessions.Find(table.GameId)!.MutateAsync(game =>
            game.Create(table.Bob, Card("Wall"), Zone.Battlefield));

        // Alice, the attacker, tries to declare Bob's blocks.
        await table.Hub.DeclareBlockers(
            table.GameId,
            new Dictionary<Guid, Guid[]> { [attacker.Value] = [blocker.Value] });

        var combat = await table.Sessions.Find(table.GameId)!.MutateAsync(game => game.State.Combat);

        Assert.False(combat.BlockersDeclared);
        Assert.Single(table.Clients.Refusals);
    }

    [Fact]
    public async Task A_player_who_is_not_seated_cannot_act_at_the_table()
    {
        // Seat membership is the authorisation, and it is taken from the token rather than the
        // message — the payload has nowhere to put a player id for exactly this reason.
        var table = await TableAsync(actingAs: Guid.NewGuid());

        await Assert.ThrowsAsync<HubException>(() =>
            table.Hub.DeclareAttackers(table.GameId, new Dictionary<Guid, AttackDto>()));
    }

    [Fact]
    public async Task Acting_in_a_game_stores_it()
    {
        // The hub is the only way a real game is ever played, so the save has to happen on this
        // path and not merely on the one the session tests drive directly.
        var store = new InMemoryGameStore();
        var sessions = new GameSessionService(NoAbilities.Instance, store);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var gameId = await sessions.CreateAsync(
        [
            new PlayerSetup(alice, "Alice", 20, [.. Enumerable.Range(1, 30).Select(i => Card($"A{i}"))]),
            new PlayerSetup(bob, "Bob", 20, [.. Enumerable.Range(1, 30).Select(i => Card($"B{i}"))]),
        ]);

        // As whoever the game is actually asking. Who goes first is decided at random
        // (CR 103.1), so a hub hard-wired to Alice answers a question put to Bob about half the
        // time — the engine refuses it, correctly, and nothing is stored. That is a flaky test
        // rather than a bug, and it only showed up in a full run.
        var choice = await sessions.Find(gameId)!.MutateAsync(game => game.State.Choice);
        Assert.NotNull(choice);

        var hub = new GameHub(sessions, NoArt, NullLogger<GameHub>.Instance)
        {
            Context = new FakeCaller(choice.PlayerId),
            Clients = new RecordingClients(),
        };

        await hub.Choose(gameId, ["keep"]);
        var stored = await store.LoadAsync(gameId);
        Assert.Contains("ChoiceMade", stored!.Log, StringComparison.Ordinal);
    }

    // ---- Transport doubles -------------------------------------------------------------------

    private sealed class FakeCaller : HubCallerContext
    {
        private readonly ClaimsPrincipal _user;

        public FakeCaller(Guid playerId) =>
            _user = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, playerId.ToString())], "test"));

        public override string ConnectionId => "connection-1";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => _user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }

    private sealed class RecordingClients : IHubCallerClients
    {
        public List<string> Refusals { get; } = [];
        public List<string> GroupSends { get; } = [];
        public List<Guid> ViewedBy { get; } = [];

        public IClientProxy Caller => new Proxy(this, Recipient.Caller, null);
        public IClientProxy Others => new Proxy(this, Recipient.Other, null);
        public IClientProxy Client(string connectionId) => new Proxy(this, Recipient.Other, null);
        public IClientProxy All => new Proxy(this, Recipient.Other, null);
        public IClientProxy AllExcept(IReadOnlyList<string> excluded) => new Proxy(this, Recipient.Other, null);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new Proxy(this, Recipient.Other, null);
        public IClientProxy Group(string groupName) => new Proxy(this, Recipient.Group, null);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new Proxy(this, Recipient.Group, null);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded) =>
            new Proxy(this, Recipient.Group, null);
        public IClientProxy OthersInGroup(string groupName) => new Proxy(this, Recipient.Group, null);
        public IClientProxy User(string userId) => new Proxy(this, Recipient.User, userId);
        public IClientProxy Users(IReadOnlyList<string> userIds) => new Proxy(this, Recipient.User, null);

        private enum Recipient
        {
            Caller,
            Group,
            User,
            Other,
        }

        private sealed class Proxy : ISingleClientProxy
        {
            private readonly RecordingClients _owner;
            private readonly Recipient _to;
            private readonly string? _userId;

            public Proxy(RecordingClients owner, Recipient to, string? userId)
            {
                _owner = owner;
                _to = to;
                _userId = userId;
            }

            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            {
                if (_to == Recipient.Caller && method == "Refused")
                    _owner.Refusals.Add(args[0] as string ?? string.Empty);

                if (_to == Recipient.Group)
                    _owner.GroupSends.Add(method);

                if (_to == Recipient.User && method == "State" && Guid.TryParse(_userId, out var id))
                    _owner.ViewedBy.Add(id);

                return Task.CompletedTask;
            }

            public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken ct = default) =>
                Task.FromResult<T>(default!);
        }
    }
}
