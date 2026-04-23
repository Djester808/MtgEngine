using System.Text.Json.Serialization;
using MtgEngine.Api.Data;
using MtgEngine.Api.Hubs;
using MtgEngine.Api.Services;
using Microsoft.EntityFrameworkCore;

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

// SignalR
builder.Services.AddSignalR(opts =>
{
    opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
    opts.MaximumReceiveMessageSize = 64 * 1024;
})
.AddJsonProtocol(o =>
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

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

// Background worker: downloads/refreshes bulk files on startup and daily
builder.Services.AddHostedService<BulkDataRefreshWorker>();

// ---- Game services ---------------------------------------
builder.Services.AddSingleton<GameSessionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GameSessionService>());
builder.Services.AddScoped<IDeckBuilderService, DeckBuilderService>();

// ---- Database --------------------------------------------
builder.Services.AddDbContext<MtgEngineDbContext>(options =>
    options.UseSqlite("Data Source=mtgengine.db"));

builder.Services.AddScoped<ICollectionService, CollectionService>();

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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AngularDev");
app.UseAuthorization();

app.MapControllers();
app.MapHub<GameHub>("/hubs/game");

app.Run();
