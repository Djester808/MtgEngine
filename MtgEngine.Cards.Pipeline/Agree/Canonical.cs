using System.Reflection;
using System.Text.Json.Nodes;

namespace MtgEngine.Cards.Pipeline.Agree;

/// <summary>
/// One spelling per reading, so that agreement measures what two models understood rather than
/// how verbose they were. The 200-face pilot found 27 of its 37 first differences were one model
/// writing a default explicitly and the other omitting it; none of those is a disagreement about
/// the card, and every one of them cost a card its <see cref="ImplementationStatus.Translated"/>.
///
/// Three kinds of rewrite, and nothing else:
/// <list type="number">
/// <item>contextual defaults written out in full are removed — a <c>who</c> of You where absent
/// already means you (CR 109.5 "you" is the controller of the ability), an <c>in</c> of
/// Battlefield on a filter the engine already reads as the battlefield
/// (<c>Query.ZoneOf(filter) = filter.In ?? Battlefield</c>), an <c>in</c> of Library on the
/// filter of a Search, whose own <c>from</c> already says Library;</item>
/// <item>free-text keys are renumbered k1, k2 … by order of first appearance within a face, so
/// "color" and "chosenColor" stop being a difference;</item>
/// <item>constructs with exactly one other spelling collapse to it: GrantAbility of a keyword is
/// GrantKeyword, a Sequence of one step is that step, an empty <c>of</c>/<c>except</c> object is
/// absent.</item>
/// </list>
///
/// Nothing semantic is touched. <c>upTo</c> present against absent, <c>anyColor</c> true against
/// false, <c>selfOnly</c> true against false and Compare against Controls are all real
/// disagreements and stay differences. "Any target" now has a spelling of its own,
/// <see cref="AnyTarget"/> (CR 115.4), so the two ways of writing the phrase across DealDamage's
/// To and ToPlayer collapse to it — but only those two: an open filter beside a player target
/// means something else and is left exactly as written.
/// </summary>
public static partial class Canonical
{
    /// <summary>The canonical reading of a definition: the same card, spelled one way.</summary>
    public static CardDefinition Of(CardDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var tree = JsonNode.Parse(CardFileSerializer.WriteDefinition(definition))
            ?? throw new InvalidOperationException("A definition serialized to JSON null.");
        return CardFileSerializer.ReadDefinition(Of(tree).ToJsonString());
    }

    /// <summary>The same rewrite on the serialized tree, which is what the diff compares.</summary>
    public static JsonNode Of(JsonNode tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var root = tree.DeepClone();
        root = Rewrite(root, Context.Root) ?? root;

        // Keys are named per face: a face is the unit a translator reads, and a key defined on
        // one face is never read from another.
        if (root is JsonObject definition && definition["faces"] is JsonArray faces)
        {
            foreach (var face in faces)
                RenameKeys(face);
        }
        else
        {
            RenameKeys(root);
        }

        return root;
    }

    /// <summary>Where in the definition a node sits, which is what makes a default a default.</summary>
    private readonly record struct Context(bool InCost, string? DefaultZone, bool InOwnLibrarySearch = false)
    {
        /// <summary>A card's abilities describe permanents until an effect says otherwise.</summary>
        public static Context Root => new(false, nameof(Zone.Battlefield));

        public Context For(JsonObject parent, string? kind, string property)
        {
            var inCost = InCost || (kind is not null && CostKinds.Value.Contains(kind));
            var zone = DefaultZone;
            var ownLibrary = false;
            if (kind == nameof(Search))
            {
                var searched = property == "for";
                zone = searched ? nameof(Zone.Library) : null;
                ownLibrary = searched && SearchesOwnLibrary(parent);
            }
            else if (kind is not null && ZoneBound.Contains(kind))
            {
                zone = null;
            }

            return new Context(inCost, zone, ownLibrary);
        }
    }

    /// <summary>
    /// Effects that move objects between zones or read another zone: inside one, the battlefield
    /// is not the zone a bare filter means, so no <c>in</c> is stripped there.
    /// </summary>
    private static readonly HashSet<string> ZoneBound = new(StringComparer.Ordinal)
    {
        nameof(Mill), nameof(Discard), nameof(Reveal), nameof(RevealUntil), nameof(Surveil),
        nameof(Scry), nameof(LookAt), nameof(Top), nameof(MayPlayFromZone), nameof(Fateseal),
        nameof(Counter), nameof(Behold), nameof(Move), nameof(ExileFromZoneCost), nameof(MillCost),
        nameof(RevealCost), nameof(DiscardCost), nameof(CollectEvidence), nameof(Learn),
    };

    /// <summary>Every string property that names a key rather than a thing.</summary>
    private static readonly string[] KeyProperties =
    [
        "key", "storeAs", "storeChoiceAs", "rememberAs", "rememberHitAs", "rememberRestAs", "chosenColorKey",
    ];

    private static readonly Lazy<HashSet<string>> YouDefaults = new(BuildYouDefaults);

    private static readonly Lazy<HashSet<string>> CostKinds = new(BuildCostKinds);

    /// <summary>
    /// The kinds with an optional <c>Who</c> of type PlayerRef defaulting to null. The default of
    /// every one of them is the controller of the ability, so a written-out You is noise. Read by
    /// reflection rather than listed, so a new verb is covered the day it is added.
    /// </summary>
    private static HashSet<string> BuildYouDefaults()
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var baseType in CardFileSerializer.PolymorphicBases)
        {
            foreach (var (type, name) in CardFileSerializer.RegisteredNames(baseType))
            {
                var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
                if (ctor is null)
                    continue;
                if (ctor.GetParameters().Any(IsOptionalYou))
                    kinds.Add(name);
            }
        }

        return kinds;
    }

    private static bool IsOptionalYou(ParameterInfo p) =>
        p.ParameterType == typeof(PlayerRef)
        && string.Equals(p.Name, "Who", StringComparison.Ordinal)
        && p.HasDefaultValue
        && p.DefaultValue is null;

    /// <summary>
    /// The cost records. A cost is paid with your own permanents and your own hand (CR 701.16a for
    /// sacrifice, 118.9 for tapping), so a filter inside one saying "you control" says nothing.
    /// </summary>
    private static HashSet<string> BuildCostKinds() =>
        new(CardFileSerializer.RegisteredNames(typeof(Cost)).Values, StringComparer.Ordinal);

    private static JsonNode? Rewrite(JsonNode? node, Context context)
    {
        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var element = array[i];
                var rewritten = Rewrite(element, context);
                if (!ReferenceEquals(rewritten, element))
                    array[i] = rewritten;
            }

            return array;
        }

        if (node is not JsonObject obj)
            return node;

        var kind = Kind(obj);
        foreach (var name in obj.Select(p => p.Key).ToList())
        {
            var value = obj[name];
            var rewritten = Rewrite(value, context.For(obj, kind, name));
            if (!ReferenceEquals(rewritten, value))
                obj[name] = rewritten;
        }

        StripDefaults(obj, kind, context);
        Derive(obj, kind, context);
        return Collapse(obj, kind) ?? obj;
    }

    private static void StripDefaults(JsonObject obj, string? kind, Context context)
    {
        // "draw a card" and "you draw a card" are the same instruction.
        if (kind is not null && YouDefaults.Value.Contains(kind) && IsYou(obj["who"]))
            obj.Remove("who");

        // "sacrifice a creature" already means one you control.
        if (context.InCost && IsYou(obj["controlledBy"]))
            obj.Remove("controlledBy");

        // Only an ObjectFilter carries "in", and only a filter has no discriminator.
        if (kind is null
            && context.DefaultZone is { } zone
            && obj["in"] is JsonValue value
            && value.TryGetValue<string>(out var written)
            && string.Equals(written, zone, StringComparison.Ordinal))
        {
            obj.Remove("in");
        }

        // An empty filter or an empty exception list says exactly what leaving it out says.
        foreach (var name in (string[])["of", "except"])
        {
            if (obj[name] is JsonObject empty && empty.Count == 0)
                obj.Remove(name);
            else if (obj[name] is JsonArray none && none.Count == 0)
                obj.Remove(name);
        }
    }

    /// <summary>A construct with exactly one other spelling, as that other spelling. Null when there is none.</summary>
    private static JsonNode? Collapse(JsonObject obj, string? kind) => kind switch
    {
        nameof(Sequence) when Only(obj, "steps") is { } step => step,
        nameof(Union) when Only(obj, "of") is { } selector => selector,
        nameof(AllOf) or nameof(AnyOf) when Only(obj, "of") is { } predicate => predicate,
        nameof(GrantAbility) => Keyworded(obj),
        _ => null,
    };

    private static JsonNode? Only(JsonObject obj, string property) =>
        obj[property] is JsonArray array && array.Count == 1 && array[0] is JsonObject one
            ? one.DeepClone()
            : null;

    /// <summary>GrantAbility of a bare keyword is GrantKeyword; the two records carry the same four fields.</summary>
    private static JsonNode? Keyworded(JsonObject obj)
    {
        if (obj["ability"] is not JsonObject ability || Kind(ability) != nameof(KeywordAbility))
            return null;

        // Detail and Condition have nowhere to go in GrantKeyword, so those stay as they are.
        if (ability["detail"] is not null || ability["condition"] is not null)
            return null;

        var granted = new JsonObject { ["kind"] = nameof(GrantKeyword) };
        foreach (var name in (string[])["keyword", "cost", "n", "of"])
        {
            if (ability[name] is { } value)
                granted[name] = value.DeepClone();
        }

        return granted;
    }

    private static void RenameKeys(JsonNode? face)
    {
        var order = new List<string>();
        Collect(face, order);
        if (order.Count == 0)
            return;

        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
            renamed[order[i]] = "k" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        Apply(face, renamed);
    }

    private static void Collect(JsonNode? node, List<string> order)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var child in array)
                    Collect(child, order);
                break;
            case JsonObject obj:
                foreach (var (name, value) in obj)
                {
                    if (IsKeyProperty(name) && value is JsonValue v && v.TryGetValue<string>(out var key) && !order.Contains(key))
                        order.Add(key);
                    Collect(value, order);
                }

                break;
            default:
                break;
        }
    }

    private static void Apply(JsonNode? node, Dictionary<string, string> renamed)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var child in array)
                    Apply(child, renamed);
                break;
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var value = obj[name];
                    if (IsKeyProperty(name) && value is JsonValue v && v.TryGetValue<string>(out var key) && renamed.TryGetValue(key, out var to))
                        obj[name] = to;
                    else
                        Apply(value, renamed);
                }

                break;
            default:
                break;
        }
    }

    private static bool IsKeyProperty(string name) => Array.Exists(KeyProperties, p => string.Equals(p, name, StringComparison.Ordinal));

    private static bool IsYou(JsonNode? node) => node is JsonObject obj && obj.Count == 1 && Kind(obj) == nameof(You);

    private static string? Kind(JsonObject obj) =>
        obj["kind"] is JsonValue value && value.TryGetValue<string>(out var kind) ? kind : null;
}
