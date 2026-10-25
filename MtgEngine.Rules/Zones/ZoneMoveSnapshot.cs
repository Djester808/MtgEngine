using System.Text.Json;
using System.Text.Json.Serialization;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

/// <summary>Private, replayable evaluation board for an interrupted simultaneous move.</summary>
internal static class ZoneMoveSnapshot
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Capture(GameState state) => JsonSerializer.Serialize(state with { Pending = null }, Options);

    public static Outcome Resume(IReplacementSystem replacements, GameState state, Resumption answer)
    {
        var resumed = replacements.Resume(Evaluate(answer.Context.ZoneMoveSnapshot, state), answer);
        return answer.Context.ZoneMoveSnapshot is null ? resumed
            : resumed with { State = GameReducer.Apply(state, resumed.Events) };
    }

    public static GameState Evaluate(string? snapshot, GameState current) => snapshot is null ? current
        : WithCurrentAllocation(JsonSerializer.Deserialize<GameState>(snapshot, Options)!, current);

    /// <summary>Keep the simultaneous evaluation board, but allocate replacement results from the live sequence.</summary>
    public static GameState WithCurrentAllocation(GameState evaluation, GameState current) => evaluation with
    {
        NextObjectId = current.NextObjectId,
        NextTimestamp = current.NextTimestamp,
        NextChoiceId = current.NextChoiceId,
        Rng = current.Rng,
    };

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(EventLogSerializer.Options) { IncludeFields = true };
        options.Converters.Insert(0, new DictionaryConverterFactory());
        return options;
    }

    // Snapshot dictionaries are entry arrays, including zone and tuple keys. This
    // format is local to snapshots; it does not change the existing event-log format.
    private sealed class DictionaryConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType
            && typeToConvert.GetGenericTypeDefinition() == typeof(ImmutableDictionary<,>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(DictionaryConverter<,>).MakeGenericType(typeToConvert.GetGenericArguments()))!;
    }

    private sealed class DictionaryConverter<TKey, TValue> : JsonConverter<ImmutableDictionary<TKey, TValue>> where TKey : notnull
    {
        public override ImmutableDictionary<TKey, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<KeyValuePair<TKey, TValue>[]>(ref reader, options)!.ToImmutableDictionary();

        public override void Write(Utf8JsonWriter writer, ImmutableDictionary<TKey, TValue> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}
