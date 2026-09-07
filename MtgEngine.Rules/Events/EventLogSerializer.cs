using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MtgEngine.Rules.State;

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
        options.Converters.Add(new ObjectIdConverter());
        options.Converters.Add(new PlayerIdConverter());
        foreach (var converter in Cards.CardFileSerializer.Options.Converters)
            options.Converters.Add(converter);
        return options;
    }

    /// <summary>
    /// An id is a bare number in the log, as a value and as a dictionary key: the declarations of
    /// combat (CR 508, 509) are keyed by object id, and System.Text.Json refuses a struct key
    /// without a converter that names it as a property.
    /// </summary>
    private sealed class ObjectIdConverter : JsonConverter<ObjectId>
    {
        public override ObjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt32());

        public override void Write(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);

        public override ObjectId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(int.Parse(reader.GetString() ?? throw new JsonException("An object id key must be a number."), CultureInfo.InvariantCulture));

        public override void WriteAsPropertyName(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions options) =>
            writer.WritePropertyName(value.Value.ToString(CultureInfo.InvariantCulture));
    }

    private sealed class PlayerIdConverter : JsonConverter<PlayerId>
    {
        public override PlayerId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt32());

        public override void Write(Utf8JsonWriter writer, PlayerId value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);

        public override PlayerId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(int.Parse(reader.GetString() ?? throw new JsonException("A player id key must be a number."), CultureInfo.InvariantCulture));

        public override void WriteAsPropertyName(Utf8JsonWriter writer, PlayerId value, JsonSerializerOptions options) =>
            writer.WritePropertyName(value.Value.ToString(CultureInfo.InvariantCulture));
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
