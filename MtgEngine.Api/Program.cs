using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MtgEngine.Api.Data;
using MtgEngine.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- Services --------------------------------------------

builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "MTG Engine API", Version = "v1" });
});

// ---- HTTP clients ----------------------------------------

// Individual Scryfall API calls (card lookups, fallback)
builder.Services.AddHttpClient("ScryfallApi", client =>
{
    client.BaseAddress = new Uri("https://api.scryfall.com/");
    client.DefaultRequestHeaders.Add("User-Agent", "MtgEngine/0.1 (contact@example.com)");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(15);
});

// Bulk-data downloads — large files, longer timeout, auto-decompress
builder.Services.AddHttpClient("ScryfallBulk", client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "MtgEngine/0.1 (contact@example.com)");
    client.Timeout = TimeSpan.FromMinutes(20);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression =
        System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
});

// ---- Scryfall / card data --------------------------------

// ScryfallService: live API + disk cache (used as fallback by BulkDataService)
builder.Services.AddSingleton<ScryfallService>();

// BulkDataService: primary IScryfallService — serves from bulk files, falls back to ScryfallService
builder.Services.AddSingleton<BulkDataService>();
builder.Services.AddSingleton<IScryfallService>(sp => sp.GetRequiredService<BulkDataService>());

// Same instance behind the narrower interface, for the callers that only resolve cards
// and never scan the corpus. They ask for less; they still get the bulk implementation.
builder.Services.AddSingleton<ICardLookup>(sp => sp.GetRequiredService<BulkDataService>());

// Background worker: downloads/refreshes bulk files on startup and daily
builder.Services.AddHostedService<BulkDataRefreshWorker>();

// ---- Deck services ---------------------------------------
builder.Services.AddScoped<IDeckBuilderService, DeckBuilderService>();

// ---- Database --------------------------------------------
builder.Services.AddDbContext<MtgEngineDbContext>(options =>
    options.UseSqlite("Data Source=mtgengine.db"));

builder.Services.AddScoped<ICollectionService, CollectionService>();
builder.Services.AddScoped<IForumService, ForumService>();
builder.Services.AddScoped<ICommanderStatsService, CommanderStatsService>();

// ---- Anthropic API ---------------------------------------
// The AI services are scoped, so their key guard would not fire until the first
// AI request. Validate eagerly here so a misconfigured deployment fails at boot.
SecretConfig.AnthropicApiKey(builder.Configuration);

builder.Services.AddHttpClient("AnthropicApi", client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
    // The resilience pipeline owns all timeouts (per-attempt and total). A finite
    // HttpClient.Timeout here would cancel the whole pipeline mid-retry.
    client.Timeout = Timeout.InfiniteTimeSpan;
})
.AddAnthropicResilience();
// One place that turns service exceptions into status codes, so every AI endpoint
// reports failure identically instead of each action repeating its own try/catch.
builder.Services.AddExceptionHandler<AiExceptionHandler>();
builder.Services.AddProblemDetails();

// Singleton: the doctrine is read from disk once and is immutable thereafter. Every AI
// pass reasons from this one copy, which is the whole point -- when the passes each held
// their own idea of "good", one card came back 85% in one list and 55% in another.
builder.Services.AddSingleton<ICommanderDoctrine, CommanderDoctrine>();

// The one path to the Messages API. Owns the auth header, the API version, the
// error-to-exception mapping and the prompt-cache logging, so the services above it
// hold a prompt and a model id rather than an HttpClient.
builder.Services.AddSingleton<IAnthropicClient, AnthropicClient>();

builder.Services.AddScoped<IAiCacheService, AiCacheService>();

// Reads each commander's text once and returns its requirements as structured data, so no
// phrasing has to be enumerated in code. Cached per commander.
builder.Services.AddScoped<ICommanderAnalysis, CommanderAnalysis>();
builder.Services.AddScoped<ICandidateRanking, CandidateRanking>();
builder.Services.AddScoped<IEdhrecPoolService, EdhrecPoolService>();
builder.Services.AddScoped<CardVisionService>();
builder.Services.AddScoped<ISynergyService, SynergyService>();
builder.Services.AddScoped<IDeckSuggestionsService, DeckSuggestionsService>();
builder.Services.AddScoped<IManaFineTuneService, ManaFineTuneService>();
builder.Services.AddScoped<IAiBuildService, AiBuildService>();

// EDHREC JSON — community deck card recommendations
builder.Services.AddHttpClient("EdhrecApi", client =>
{
    client.BaseAddress = new Uri("https://json.edhrec.com/");
    client.DefaultRequestHeaders.Add("User-Agent", "MtgEngine/0.1 (contact@example.com)");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(20);
});

// ---- Deck import -----------------------------------------
builder.Services.AddHttpClient("DeckImport", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.Add("User-Agent", "MtgEngineApp/1.0");
    client.DefaultRequestHeaders.Add("X-Moxfield-Version", "2.0");
});
builder.Services.AddScoped<DeckImportService>();
builder.Services.AddScoped<CommanderDeckSeeder>();

// ---- Auth ------------------------------------------------
builder.Services.AddScoped<TokenService>();

var jwtKey = Encoding.UTF8.GetBytes(SecretConfig.JwtSecret(builder.Configuration));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(jwtKey),
            ValidateIssuer = false,
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero,
        };
    });

// ---- CORS ------------------------------------------------
builder.Services.AddCors(opts =>
{
    opts.AddPolicy("AngularDev", policy =>
        policy
            .WithOrigins("http://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
});

// ---- Build -----------------------------------------------

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
    await db.Database.MigrateAsync();
}

// Must precede the endpoints it protects.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AngularDev");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
