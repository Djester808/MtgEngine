using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MtgEngine.Api.Hubs;
using MtgEngine.Api.Services;
using MtgEngine.Cards;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;
using CardDefinition = MtgEngine.Cards.CardDefinition;

// Isolated, loopback-only verification host. Uses the production engine, session and hub;
// four separate origins keep browser accounts independent. No application database is used.
var builder = WebApplication.CreateBuilder(args);
var dense = builder.Configuration.GetValue<bool>("dense");
var cleanup = builder.Configuration.GetValue<bool>("cleanup");
var returns = builder.Configuration.GetValue<bool>("returns");
var lobbyMode = builder.Configuration.GetValue<bool>("lobby");
var libraryOpening = builder.Configuration.GetValue<bool>("libraryOpening");
var libraryTopLand = builder.Configuration.GetValue<bool>("libraryTopLand");
var libraryPhasing = builder.Configuration.GetValue<bool>("libraryPhasing");
var libraryTopPlay = builder.Configuration.GetValue<bool>("libraryTopPlay") || libraryTopLand || libraryPhasing;
var libraryDraw = builder.Configuration.GetValue<bool>("libraryDraw") || libraryTopPlay;
var libraryShuffle = builder.Configuration.GetValue<bool>("libraryShuffle");
var libraryPrivateShuffle = builder.Configuration.GetValue<bool>("libraryPrivateShuffle");
var libraryDisclosureShuffle = builder.Configuration.GetValue<bool>("libraryDisclosureShuffle") || libraryPrivateShuffle;
var unavailableArtwork = builder.Configuration.GetValue<bool>("unavailableArtwork");
var libraryActiveSeat = builder.Configuration.GetValue<int?>("libraryActiveSeat") ?? 0;
if (libraryActiveSeat is < 0 or > 3)
    throw new ArgumentOutOfRangeException(nameof(libraryActiveSeat));
var libraryMode = builder.Configuration.GetValue<bool>("library") || libraryShuffle || libraryDraw;
var librarySeats = builder.Configuration.GetValue<int?>("librarySeats") ?? 2;
var libraryCount = builder.Configuration.GetValue<int?>("libraryCount");
if (libraryCount is < 0 or > 1)
    throw new ArgumentOutOfRangeException(nameof(libraryCount));
if (librarySeats is not (2 or 4))
    throw new ArgumentOutOfRangeException(nameof(librarySeats));
var firstPort = builder.Configuration.GetValue<int?>("port") ?? 4320;
if (firstPort is < 1024 or > 65532)
    throw new ArgumentOutOfRangeException(nameof(firstPort));
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls(Enumerable.Range(firstPort, 4).Select(p => $"http://localhost:{p}").ToArray());
builder.Services.AddAuthentication("table-qa").AddScheme<AuthenticationSchemeOptions, QaAuthentication>("table-qa", _ => { });
builder.Services.AddAuthorization();
if (unavailableArtwork)
{
    builder.Services.AddSingleton<ICardLookup, QaUnavailableArtwork>();
    builder.Services.AddSingleton<GameArtworkService>();
}
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
var pool = new QaPool(libraryShuffle, libraryDraw);
if (libraryDisclosureShuffle)
    pool.ConfigureDisclosureShuffle(libraryPrivateShuffle, builder.Configuration.GetValue<bool>("libraryAllDisclosure"));
if (libraryTopPlay)
    pool.ConfigureTopPlay(libraryPhasing);
var logStore = new QaLogStore();
var sessions = new GameSessionService(logStore, pool, new DeckGate(pool));
var original = await sessions.CreateAsync(new NewGameRequest(
    Enumerable.Range(0, libraryMode || libraryOpening ? librarySeats : 4).Select(i => new NewGameSeat($"qa{i}", new[] { "Alex", "Mira", "Theo", "Jules" }[i],
        (libraryOpening ? Enumerable.Range(0, 99).Select(_ => "forest")
            : Enumerable.Range(0, dense ? 16 : 8).SelectMany(_ => pool.Deck))
            .Select(id => new StartingCard(id)).ToArray(), ["leader"])).ToArray(), 42, libraryShuffle || libraryDraw, "commander", 40));
while (!libraryOpening && original.State.Pending is { } pending)
{
    var kept = await sessions.ApplyAsync(original, new AnswerChoice(pending.Player, pending.Id, ["keep"]));
    if (kept is MtgEngine.Rules.Engine.Refused refused)
        throw new InvalidOperationException(refused.Reason);
}

// Build an explicit replay fixture with ordinary zone-system transitions, then resume it
// through the session service. Every action after setup goes through the production hub.
var state = original.State;
var log = original.Log;
var zones = new ZoneSystem();
if (!libraryOpening)
{
    void Place(PlayerId owner, string definition, Zone destination)
    {
        var candidate = state.Objects.Values.First(o => o.Owner == owner && o.Definition.OracleId == definition && o.Zone.Zone == Zone.Library);
        var outcome = zones.Move(state, candidate.Id,
            destination is Zone.Hand or Zone.Graveyard or Zone.Library ? ZoneKey.Of(destination, owner) : ZoneKey.Shared(destination), controller: owner);
        state = outcome.State;
        log = log.AddRange(outcome.Events);
    }
    foreach (var seat in state.Players)
    {
        foreach (var id in state.In(ZoneKey.Of(Zone.Hand, seat.Id)).ToArray())
        {
            var outcome = zones.Move(state, id, ZoneKey.Of(Zone.Library, seat.Id));
            state = outcome.State;
            log = log.AddRange(outcome.Events);
        }
        foreach (var definition in new[] { "forest", "forest", "island", "mountain", "swamp", "scout", "scout", "relic" })
            Place(seat.Id, definition, Zone.Battlefield);
        if (dense)
        {
            foreach (var definition in Enumerable.Repeat("forest", 10)
                .Concat(Enumerable.Repeat("island", 3)).Concat(Enumerable.Repeat("mountain", 3))
                .Concat(Enumerable.Repeat("swamp", 3)).Concat(Enumerable.Repeat("scout", 6))
                .Concat(Enumerable.Repeat("relic", 3)))
                Place(seat.Id, definition, Zone.Battlefield);
            for (var token = 0; token < 6; token++)
            {
                var created = zones.CreateToken(state,
                    new TokenSpec("Saproling", Color.Green, CardType.Creature, ["Saproling"], Power: 1, Toughness: 1),
                    seat.Id, tapped: token % 2 == 0, attacking: false);
                state = created.State;
                log = log.AddRange(created.Events);
            }
        }
        foreach (var definition in new[] { "spark", "insight", "demise", "scout", "forest" })
            Place(seat.Id, definition, Zone.Hand);
        if (cleanup && seat.Id == new PlayerId(0))
            foreach (var definition in new[] { "insight", "demise", "forest" })
                Place(seat.Id, definition, Zone.Hand);
        Place(seat.Id, "insight", Zone.Graveyard);
        Place(seat.Id, "relic", Zone.Exile);
    }
    if (libraryTopPlay)
    {
        var zone = ZoneKey.Of(Zone.Library, new PlayerId(0));
        var cards = state.In(zone);
        var required = libraryTopLand ? 2 : 1;
        var first = cards.Where(id => state.Objects[id].Definition.OracleId == (libraryTopLand ? "forest" : "scout")).Take(required).ToImmutableList();
        if (first.Count != required)
            throw new InvalidOperationException("The top-play fixture needs its starting cards.");
        var reorder = new ZoneReordered(zone, first.AddRange(cards.Except(first)));
        state = reorder.Apply(state);
        log = log.Add(reorder);
    }
    foreach (var obj in state.Objects.Values.Where(o => o.Zone.Zone == Zone.Battlefield))
    {
        var ready = new ObjectChanged(obj.Id, SummoningSick: false);
        state = ready.Apply(state);
        log = log.Add(ready);
    }
    log = log.Add(new StepBegan(cleanup ? Step.End : Step.PrecombatMain)).Add(new PriorityGranted(new PlayerId(0)));
    if (libraryDisclosureShuffle)
    {
        log = log.Add(new TurnBegan(1, new PlayerId(libraryActiveSeat), false))
            .Add(new StepBegan(Step.PrecombatMain)).Add(new PriorityGranted(new PlayerId(libraryActiveSeat)));
        log = log.Add(new ObjectCreated(new GameObject(new ObjectId(state.NextObjectId), pool.ByOracleId("insight")!,
            0, new PlayerId(0), new PlayerId(0), ZoneKey.Shared(Zone.Stack), state.NextTimestamp)
        { Stack = new StackEntry(null, null, [], [], [], null, false) }, -1));
    }
    if (returns)
    {
        foreach (var leader in state.Objects.Values.Where(card => card.IsCommander).OrderBy(card => card.Owner.Value).ToArray())
        {
            var moved = zones.Move(state, leader.Id,
                leader.Owner.Value % 2 == 0 ? ZoneKey.Of(Zone.Graveyard, leader.Owner) : ZoneKey.Shared(Zone.Exile));
            state = moved.State;
            log = log.AddRange(moved.Events);
        }
        state = GameReducer.Replay(log);
        var checkedState = MtgEngine.Rules.Engine.Subsystems.Wave2().Sba.Check(state);
        log = log.AddRange(checkedState.Events);
    }
    if (libraryCount is { } count)
    {
        foreach (var id in state.In(ZoneKey.Of(Zone.Library, new PlayerId(0))).Skip(count).ToArray())
        {
            var moved = zones.Move(state, id, ZoneKey.Shared(Zone.Exile));
            state = moved.State;
            log = log.AddRange(moved.Events);
        }
    }
}
var row = logStore.Rows[original.Id];
row.LogJson = EventLogSerializer.Write(log);
row.EventCount = log.Count;
sessions = new GameSessionService(logStore, pool, new DeckGate(pool));
var table = await sessions.FindAsync(original.Id) ?? throw new InvalidOperationException("Fixture did not replay.");
builder.Services.AddSingleton(sessions);
if (lobbyMode)
    await QaLobby.ConfigureAsync(builder.Services, pool);
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
if (unavailableArtwork)
    app.MapGet("/api/qa/unavailable-art", () => Results.NotFound());
if (lobbyMode)
    QaLobby.Map(app);
app.MapPost("/api/auth/login", (QaLogin login) =>
    login.Username is "qa0" or "qa1" or "qa2" or "qa3"
        ? Results.Ok(new { Token = login.Username, Username = login.Username })
        : Results.Unauthorized());
app.MapPost("/api/auth/logout", () => Results.NoContent());
app.MapGet("/api/qa/table", () => new { GameId = table.Id, Dense = dense, Cleanup = cleanup, Returns = returns, Accounts = new[] { "qa0", "qa1", "qa2", "qa3" } });
if (libraryPhasing)
    app.MapGet("/api/qa/projection-timing", () =>
    {
        var engine = MtgEngine.Rules.Engine.Subsystems.Wave2();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine("QA_PROJECTION_START");
        _ = engine.Views.Project(table.State, new PlayerId(0));
        var viewMs = watch.ElapsedMilliseconds;
        Console.WriteLine($"QA_VIEW_MS={viewMs}");
        _ = engine.Legal.For(table.State, new PlayerId(0));
        Console.WriteLine($"QA_LEGAL_MS={watch.ElapsedMilliseconds - viewMs}");
        return Results.Ok(new { ViewMs = viewMs, TotalMs = watch.ElapsedMilliseconds });
    }).RequireAuthorization();
// Isolated QA-only oracle for exact hidden order; never exposed by the production API.
app.MapGet("/api/qa/library-orders", () => table.State.Players.Select(player =>
    table.State.In(ZoneKey.Of(Zone.Library, player.Id)).Select(id => id.Value).ToArray()).ToArray()).RequireAuthorization();
app.MapGet("/api/qa/library-events", () => new
{
    Searches = table.Log.OfType<LibrarySearched>().Count(),
    Draws = table.Log.OfType<ObjectDrawn>().Count(),
    EmptyDraws = table.Log.OfType<DrawFromEmptyLibraryAttempted>().Count(),
    Shuffles = table.Log.OfType<ZoneReordered>().Count(change => change.Shuffle),
    Reorders = table.Log.OfType<ZoneReordered>().Count(change => !change.Shuffle),
    Reveals = table.Log.OfType<CardsRevealed>().Count(),
    Resolved = table.Log.OfType<StackObjectResolved>().Count(),
    Triggers = table.Log.OfType<MtgEngine.Rules.Triggers.TriggerFired>().Count(),
    TokensCreatedThisTurn = table.State.History.TokensCreated.Values.Sum(tokens => tokens.Count),
    TokenDeathsThisTurn = table.State.History.Died.Count(card => card.IsToken),
}).RequireAuthorization();
app.MapGet("/api/games/{id:guid}", async (Guid id, ClaimsPrincipal user) =>
{
    var session = await sessions.FindAsync(id);
    if (session is null || session.SeatOf(user.FindFirstValue(ClaimTypes.NameIdentifier)) is not { } seat)
        return Results.NotFound();
    var (view, legal) = session.ProjectFor(seat);
    return Results.Ok(new { GameId = id, View = view, Legal = legal });
}).RequireAuthorization();
app.MapHub<GameHub>("/hubs/game");
var clientPath = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "../../../mtg-client/dist/mtg-client/browser"));
var files = new PhysicalFileProvider(clientPath);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
app.MapFallback(async context =>
{
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(Path.Combine(clientPath, "index.html"));
});
Console.WriteLine($"TABLE_QA_GAME={table.Id}");
await app.RunAsync();

public sealed record QaLogin(string Username, string Password);

public sealed class QaAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty, StringComparison.Ordinal);
        if (token.Length == 0)
            token = Request.Query["access_token"].ToString();
        if (token is not ("qa0" or "qa1" or "qa2" or "qa3"))
            return Task.FromResult(AuthenticateResult.NoResult());
        var account = configuration.GetValue<bool>("lobby") ? QaLobby.Account(int.Parse(token[2..])).ToString() : token;
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account)], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, Scheme.Name)));
    }
}

public sealed class QaLogStore : IGameLogStore
{
    public Dictionary<Guid, PersistedGame> Rows { get; } = [];

    public Task<PersistedGame?> LoadAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Rows.GetValueOrDefault(id));

    public Task<bool> TryCreateAsync(PersistedGame game, CancellationToken ct = default) => Task.FromResult(Rows.TryAdd(game.Id, game));

    public Task AppendAsync(Guid id, string eventsJson, int expectedCount, int addedCount, bool finished, CancellationToken ct = default)
    {
        var row = Rows[id];
        if (row.EventCount != expectedCount)
            throw new InvalidOperationException("Game revision changed.");
        row.LogJson = EventLogSerializer.Write(EventLogSerializer.Read(row.LogJson).Concat(EventLogSerializer.Read(eventsJson)));
        row.EventCount = expectedCount + addedCount;
        row.Finished = finished;
        return Task.CompletedTask;
    }
}

public sealed class QaPool : ICardFilePool
{
    private readonly Dictionary<string, CardDefinition> _cards = [];

    public string[] Deck { get; } = ["forest", "island", "mountain", "swamp", "scout", "relic", "spark", "insight", "demise"];

    public QaPool(bool libraryShuffle = false, bool libraryDraw = false)
    {
        foreach (var (name, symbol) in new[] { ("Forest", "G"), ("Island", "U"), ("Mountain", "R"), ("Swamp", "B") })
            Add(name.ToLowerInvariant(), new Face(name, FaceRole.Main, new TypeLine(CardType.Land, Supertype.Basic, [name]),
                Abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: symbol))) { Text = $"Tap: Add {{{symbol}}}." }], OracleText: $"Tap: Add {{{symbol}}}."));
        Add("scout", new Face("Canopy Scout", FaceRole.Main, new TypeLine(CardType.Creature, Supertype.None, ["Scout"]), "{1}{G}", new PrintedStat("2"), new PrintedStat("2"), OracleText: "A sample creature."));
        Add("relic", new Face("Wayfarer's Relic", FaceRole.Main, new TypeLine(CardType.Artifact, Supertype.None, []), "{1}", OracleText: "Sample artifact."));
        if (libraryShuffle || libraryDraw)
        {
            var replacement = new ReplacementAbility(libraryDraw ? new WouldDraw(new You()) : new WouldShuffle(new You()), new ModifyAmount(Add: 0));
            Add("relic", new Face(libraryDraw ? "Library draw fixture" : "Library shuffle fixture", FaceRole.Main, new TypeLine(CardType.Artifact),
                Abilities: [replacement, replacement], OracleText: "QA fixture: two competing shuffle replacements."));
        }
        Add("spark", new Face("Cinder Spark", FaceRole.Main, new TypeLine(CardType.Instant, Supertype.None, []), "{R}", Abilities: [new SpellAbility(new DealDamage(new Fixed(3), new AnyTarget()))], OracleText: "Deal 3 damage to any target."));
        Add("insight", new Face("Sudden Insight", FaceRole.Main, new TypeLine(CardType.Instant, Supertype.None, []), "{1}{U}", Abilities: [new SpellAbility(new Draw(new Fixed(2)))], OracleText: "Draw two cards."));
        Add("demise", new Face("Quiet Demise", FaceRole.Main, new TypeLine(CardType.Instant, Supertype.None, []), "{1}{B}", Abilities: [new SpellAbility(new Destroy(new Target(new ObjectFilter(Types: CardType.Creature))))], OracleText: "Destroy target creature."));
        Add("leader", new Face("Keeper of the Horizon", FaceRole.Main, new TypeLine(CardType.Creature, Supertype.Legendary, []), "{3}{G}", new PrintedStat("4"), new PrintedStat("5")));
    }

    private void Add(string id, Face face) => _cards[id] = new CardDefinition(id, face.Name, Layout.Normal, [face]);

    public void ConfigureTopPlay(bool phasing = false)
    {
        Add("relic", new Face("Library permission fixture", FaceRole.Main, new TypeLine(CardType.Artifact),
            Abilities: [new StaticAbility(new MayPlayFromZone(new You(), Zone.Library, TopOfLibraryOnly: true))],
            OracleText: "QA fixture: you may play the top card of your library."));
        if (phasing)
            Add("relic", new Face("Library phasing provider", FaceRole.Main, new TypeLine(CardType.Artifact),
                Abilities: [
                    new ActivatedAbility([], new PhaseOut(new Each(ObjectFilter.Any with { Named = "Library phasing provider", ControlledBy = new You() }))),
                    new StaticAbility(new GrantAbility(new StaticAbility(new MayPlayFromZone(new You(), Zone.Library, TopOfLibraryOnly: true))),
                        To: new Each(ObjectFilter.Any with { In = Zone.Battlefield, Types = CardType.Creature, OwnedBy = new You() })),
                    new StaticAbility(new MayLookAtTopCard(new You())),
                ], OracleText: "QA fixture: grants library access; activate to phase out."));
        Add("scout", new Face("Canopy Scout", FaceRole.Main, new TypeLine(CardType.Creature), "{0}",
            new PrintedStat("2"), new PrintedStat("2"), OracleText: "A sample creature."));
    }

    public void ConfigureDisclosureShuffle(bool privateLook, bool allLibraries) => Add("insight", new Face("Library disclosure fixture", FaceRole.Main,
        new TypeLine(CardType.Instant), Abilities: [new SpellAbility(new Sequence([
            privateLook ? new LookAt(new Top(new You(), new Fixed(2))) : new Reveal(new Top(allLibraries ? new EachPlayer() : new You(), new Fixed(2))),
            new May(new GainLife(new Fixed(1))),
            new Shuffle(allLibraries ? new EachPlayer() : new You()), new May(new GainLife(new Fixed(1))),
        ]))], OracleText: "QA fixture: inspect two library cards, pause, shuffle, pause."));

    public int Count => _cards.Count;

    public CardStatus StatusOf(string oracleId) => new(oracleId, _cards[oracleId].Name, ImplementationStatus.Verified);

    public CardDefinition? ByOracleId(string oracleId) => _cards.GetValueOrDefault(oracleId);

    public CardDefinition? ByName(string name) => _cards.Values.FirstOrDefault(c => c.Name == name);
}
