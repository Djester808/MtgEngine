using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MtgEngine.Cards;

/// <summary>
/// Reads and writes card files, and emits the schema the translator is constrained to. The
/// name table is the set of <see cref="JsonDerivedTypeAttribute"/>s on the polymorphic bases;
/// <see cref="PolymorphicBases"/> lists those bases so a test can prove every record is registered.
/// </summary>
public static class CardFileSerializer
{
    public static readonly IReadOnlyList<Type> PolymorphicBases =
    [
        typeof(Amount), typeof(PlayerRef), typeof(Selector), typeof(Predicate), typeof(Trigger),
        typeof(Condition), typeof(Cost), typeof(Duration), typeof(ContinuousEffect),
        typeof(ReplacedEvent), typeof(Effect), typeof(Ability), typeof(Provenance),
        typeof(ScenarioSource), typeof(ScenarioStep), typeof(Expectation),
    ];

    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            // The schema exporter refuses options with no explicit resolver; reflection is the default anyway.
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            AllowOutOfOrderMetadataProperties = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new FlagsArrayConverter<Color>());
        options.Converters.Add(new FlagsArrayConverter<CardType>());
        options.Converters.Add(new FlagsArrayConverter<Supertype>());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static CardFile Read(string json) =>
        JsonSerializer.Deserialize<CardFile>(json, Options)
        ?? throw new JsonException("The card file deserialised to null.");

    public static string Write(CardFile file) => JsonSerializer.Serialize(file, Options);

    public static CardDefinition ReadDefinition(string json) =>
        JsonSerializer.Deserialize<CardDefinition>(json, Options)
        ?? throw new JsonException("The definition deserialised to null.");

    public static string WriteDefinition(CardDefinition definition) => JsonSerializer.Serialize(definition, Options);

    /// <summary>The schema of a <see cref="CardDefinition"/>: what a translator must produce.</summary>
    public static JsonNode DefinitionSchema() => SchemaOf(typeof(CardDefinition));

    /// <summary>The schema of a whole card file, for the gate check.</summary>
    public static JsonNode FileSchema() => SchemaOf(typeof(CardFile));

    /// <summary>The schema of a scenario, for the scenario-generation stage.</summary>
    public static JsonNode ScenarioSchema() => SchemaOf(typeof(Scenario));

    private static JsonNode SchemaOf(Type type) => VocabularySchema.For(type);

    /// <summary>Every concrete record registered under a polymorphic base, with its discriminator.</summary>
    public static IReadOnlyDictionary<Type, string> RegisteredNames(Type polymorphicBase) =>
        polymorphicBase.GetCustomAttributes<JsonDerivedTypeAttribute>()
            .ToDictionary(a => a.DerivedType, a => a.TypeDiscriminator?.ToString() ?? a.DerivedType.Name);
}

/// <summary>A [Flags] enum as a JSON array of member names: ["Red","Green"] rather than "Red, Green".</summary>
public sealed class FlagsArrayConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return Parse(reader.GetString() ?? string.Empty);

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected an array of {typeof(TEnum).Name} names.");

        long bits = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException($"Expected a {typeof(TEnum).Name} name.");
            bits |= System.Convert.ToInt64(Parse(reader.GetString() ?? string.Empty), System.Globalization.CultureInfo.InvariantCulture);
        }

        return (TEnum)Enum.ToObject(typeof(TEnum), bits);
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        var bits = System.Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var member in Enum.GetValues<TEnum>())
        {
            var memberBits = System.Convert.ToInt64(member, System.Globalization.CultureInfo.InvariantCulture);
            if (memberBits != 0 && (bits & memberBits) == memberBits)
                writer.WriteStringValue(member.ToString());
        }

        writer.WriteEndArray();
    }

    private static TEnum Parse(string text)
    {
        if (Enum.TryParse<TEnum>(text.Replace(" ", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var value))
            return value;
        throw new JsonException($"'{text}' is not a {typeof(TEnum).Name}.");
    }
}
