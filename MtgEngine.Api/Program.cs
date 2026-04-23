using System.Text.Json.Serialization;
using MtgEngine.Api.Hubs;
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

// SignalR
builder.Services.AddSignalR(opts =>
{
    opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
    opts.MaximumReceiveMessageSize = 64 * 1024; // 64 KB
})
.AddJsonProtocol(o =>
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// HTTP client for Scryfall
builder.Services.AddHttpClient<IScryfallService, ScryfallService>(client =>
{
    client.BaseAddress = new Uri("https://api.scryfall.com/");
    client.DefaultRequestHeaders.Add("User-Agent", "MtgEngine/0.1 (contact@example.com)");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Game services
builder.Services.AddSingleton<GameSessionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GameSessionService>());
builder.Services.AddScoped<IDeckBuilderService, DeckBuilderService>();

// CORS — allow Angular dev server
builder.Services.AddCors(opts =>
{
    opts.AddPolicy("AngularDev", policy =>
    {
        policy
            .WithOrigins("http://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials(); // required for SignalR
    });
});

// ---- Build -----------------------------------------------

var app = builder.Build();

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
