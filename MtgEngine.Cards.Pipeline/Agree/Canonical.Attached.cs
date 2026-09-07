using System.Text.Json.Nodes;

namespace MtgEngine.Cards.Pipeline.Agree;

/// <summary>
/// The fourth canonical pass, from V9's seam request: three pilot cards spell "whenever equipped
/// creature dies / attacks / deals combat damage" as a trigger filter of <c>Also: AttachedTo(Self)</c>,
/// and by the <see cref="AttachedTo"/> doc (V8) that predicate runs the other way — it admits the
/// Auras and Equipment hanging on this permanent, never the creature this permanent hangs on. Those
/// cards verified only because the query implemented the inverted direction; E5h turns it the right
/// way round this wave, and the spelling that survives the turn is <c>IsOneOf(Attached)</c>
/// (CR 301.5c names the equipped creature from the Equipment's side). The two Aura cards that wrote
/// "whenever enchanted land is tapped for mana" the same way are the same rewrite.
///
/// The rewrite is confined to a trigger's own filters. An <c>AttachedTo(Self)</c> in an effect's
/// filter — "destroy all Auras attached to it" — is the genuine reading of that phrase and is left
/// exactly as written, so this pass cannot turn one card into another.
/// </summary>
public static partial class Canonical
{
    /// <summary>
    /// <c>AttachedTo(Self)</c> as a trigger filter's predicate, or as one member of an AllOf/AnyOf
    /// there, becomes <c>IsOneOf(Attached)</c>. Deeper nesting is left alone: a predicate under a
    /// NotPredicate says "not the equipped creature", which no pilot card wrote and which this pass
    /// does not guess at.
    /// </summary>
    private static void RewriteEquippedCreatureInTriggerFilter(JsonObject obj, string? kind)
    {
        if (kind is null || !TriggerFilters.Value.TryGetValue(kind, out var properties))
            return;

        foreach (var property in properties)
        {
            if (obj[property] is not JsonObject filter)
                continue;

            if (IsAttachedToSelf(filter["also"]))
            {
                filter["also"] = OneOfAttached();
            }
            else if (filter["also"] is JsonObject combined
                && Kind(combined) is nameof(AllOf) or nameof(AnyOf)
                && combined["of"] is JsonArray parts)
            {
                for (var i = 0; i < parts.Count; i++)
                {
                    if (IsAttachedToSelf(parts[i]))
                        parts[i] = OneOfAttached();
                }
            }
        }
    }

    private static bool IsAttachedToSelf(JsonNode? node) =>
        node is JsonObject predicate
        && predicate.Count == 2
        && Kind(predicate) == nameof(AttachedTo)
        && predicate["to"] is JsonObject to
        && to.Count == 1
        && Kind(to) == nameof(Self);

    private static JsonObject OneOfAttached() =>
        new() { ["kind"] = nameof(IsOneOf), ["of"] = new JsonObject { ["kind"] = nameof(Attached) } };

    private static readonly Lazy<Dictionary<string, string[]>> TriggerFilters = new(BuildTriggerFilters);

    /// <summary>
    /// Every trigger record's ObjectFilter parameters, by discriminator: the filters that describe
    /// the objects a trigger watches. Read by reflection, so a trigger added later is covered the
    /// day it is added.
    /// </summary>
    private static Dictionary<string, string[]> BuildTriggerFilters()
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (type, name) in CardFileSerializer.RegisteredNames(typeof(Trigger)))
        {
            var filters = LongestConstructor(type)?.GetParameters()
                .Where(p => p.ParameterType == typeof(ObjectFilter))
                .Select(p => Camel(p.Name!))
                .ToArray();
            if (filters is { Length: > 0 })
                map[name] = filters;
        }

        return map;
    }

    /// <summary>
    /// The triggers this pass reads a filter of, and which filters: derived, not listed, so the
    /// coverage of the rule is a thing to test and a thing to print.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> TriggerFiltersRead =>
        TriggerFilters.Value.ToDictionary(e => e.Key, e => (IReadOnlyList<string>)e.Value, StringComparer.Ordinal);
}
