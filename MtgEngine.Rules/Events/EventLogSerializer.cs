using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace MtgEngine.Rules.Events;

/// <summary>
/// Writes a game's log to text and reads it back. Event types are discovered from this
/// assembly and named by their type name; <see cref="KnownEventNames"/> is what a snapshot test
/// compares against <c>events.txt</c>, so a renamed or added event is a deliberate, reviewed
/// change rather than a stored game that cannot be read.
/// </summary>
public static class EventLogSerializer
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static IReadOnlyList<string> KnownEventNames { get; } =
        [.. EventTypes().Select(t => t.Name).Order(StringComparer.Ordinal)];

    private static IEnumerable<Type> EventTypes() =>
        typeof(GameEvent).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(GameEvent).IsAssignableFrom(t));

    private static JsonSerializerOptions Create()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(GameEvent))
                return;
            info.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "event",
                IgnoreUnrecognizedTypeDiscriminators = false,
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
            };
            foreach (var type in EventTypes())
                info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(type, type.Name));
        });

        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = resolver,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowOutOfOrderMetadataProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        foreach (var converter in Cards.CardFileSerializer.Options.Converters)
            options.Converters.Add(converter);
        return options;
    }

    public static string Write(IEnumerable<GameEvent> log) => JsonSerializer.Serialize(log.ToArray(), Options);

    public static IReadOnlyList<GameEvent> Read(string json) =>
        JsonSerializer.Deserialize<GameEvent[]>(json, Options) ?? [];

    /// <summary>A hash of the engine's event vocabulary and assembly version: what a card file's verification was run against.</summary>
    public static string EngineHash()
    {
        var version = typeof(GameEvent).Assembly.GetName().Version?.ToString() ?? "0";
        var names = string.Join(",", KnownEventNames);
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(version + "|" + names));
        return "engine:" + System.Convert.ToHexStringLower(bytes)[..16];
    }

    internal static IReadOnlyList<Type> AllEventTypes() => [.. EventTypes()];

    internal static bool IsEvent(TypeInfo type) => typeof(GameEvent).IsAssignableFrom(type);
}
