using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace MtgEngine.Cards;

/// <summary>
/// Emits a JSON Schema for a vocabulary root with one shared definition per record and per
/// polymorphic base, so the mutual recursion between effects, selectors and filters is a
/// <c>$ref</c> rather than an inlined tree. This is the contract the translator is constrained
/// to: <c>additionalProperties</c> is false everywhere, every polymorphic value carries its
/// <c>kind</c>, and the XML doc comments become descriptions so the model reads the same
/// sentence a developer would.
/// </summary>
public static class VocabularySchema
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Docs = new(LoadDocs);

    public static JsonNode For(Type root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var defs = new JsonObject();
        var pending = new Queue<Type>();
        var seen = new HashSet<Type>();

        void Visit(Type t)
        {
            if (seen.Add(t))
                pending.Enqueue(t);
        }

        Visit(root);
        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            defs[type.Name] = IsPolymorphicBase(type) ? BaseSchema(type, Visit) : RecordSchema(type, Visit);
        }

        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = root.Name,
            ["$ref"] = $"#/$defs/{root.Name}",
            ["$defs"] = defs,
        };
    }

    private static bool IsPolymorphicBase(Type type) =>
        type.IsAbstract && type.GetCustomAttributes<System.Text.Json.Serialization.JsonDerivedTypeAttribute>().Any();

    private static JsonObject BaseSchema(Type baseType, Action<Type> visit)
    {
        var options = new JsonArray();
        foreach (var (derived, _) in CardFileSerializer.RegisteredNames(baseType).OrderBy(p => p.Value, StringComparer.Ordinal))
        {
            visit(derived);
            options.Add(new JsonObject { ["$ref"] = $"#/$defs/{derived.Name}" });
        }

        var schema = new JsonObject { ["anyOf"] = options };
        Describe(schema, baseType);
        return schema;
    }

    private static JsonObject RecordSchema(Type type, Action<Type> visit)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        var discriminator = CardFileSerializer.PolymorphicBases.FirstOrDefault(b => b.IsAssignableFrom(type) && b != type);
        if (discriminator is not null)
        {
            properties["kind"] = new JsonObject { ["const"] = type.Name };
            required.Add("kind");
        }

        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        var parameters = ctor?.GetParameters().ToDictionary(p => p.Name!, StringComparer.Ordinal) ?? [];

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite && !parameters.ContainsKey(prop.Name))
                continue; // computed helpers such as AbilityList
            if (prop.Name == "EqualityContract")
                continue;

            var jsonName = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
            var nullable = IsNullable(prop);
            var schema = SchemaFor(prop.PropertyType, visit, nullable);
            Describe((JsonObject)schema, type, prop.Name);
            properties[jsonName] = schema;

            var isOptional = nullable || (parameters.TryGetValue(prop.Name, out var p) && p.HasDefaultValue);
            if (!isOptional)
                required.Add(jsonName);
        }

        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Count > 0)
            result["required"] = required;
        Describe(result, type);
        return result;
    }

    private static JsonNode SchemaFor(Type type, Action<Type> visit, bool nullable)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return SchemaFor(underlying, visit, nullable: true);

        JsonObject schema;
        if (type == typeof(string))
            schema = new JsonObject { ["type"] = "string" };
        else if (type == typeof(bool))
            schema = new JsonObject { ["type"] = "boolean" };
        else if (type == typeof(int) || type == typeof(long) || type == typeof(ulong))
            schema = new JsonObject { ["type"] = "integer" };
        else if (type == typeof(DateTimeOffset))
            schema = new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        else if (type.IsEnum && type.GetCustomAttribute<FlagsAttribute>() is not null)
            schema = FlagsSchema(type);
        else if (type.IsEnum)
            schema = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. Enum.GetNames(type).Select(n => JsonValue.Create(n))]) };
        else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
            schema = new JsonObject { ["type"] = "object", ["additionalProperties"] = SchemaFor(type.GetGenericArguments()[1], visit, false) };
        else if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type) && type.IsGenericType)
            schema = new JsonObject { ["type"] = "array", ["items"] = SchemaFor(type.GetGenericArguments()[0], visit, false) };
        else if (type.Namespace == typeof(VocabularySchema).Namespace)
        {
            visit(type);
            schema = new JsonObject { ["$ref"] = $"#/$defs/{type.Name}" };
        }
        else
            throw new NotSupportedException($"No schema mapping for {type}.");

        return nullable ? new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) } : schema;
    }

    private static JsonObject FlagsSchema(Type type)
    {
        var names = Enum.GetNames(type).Where(n => n is not "None" and not "Colorless").Select(n => JsonValue.Create(n));
        return new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. names]) },
            ["uniqueItems"] = true,
        };
    }

    private static bool IsNullable(PropertyInfo prop)
    {
        if (Nullable.GetUnderlyingType(prop.PropertyType) is not null)
            return true;
        if (prop.PropertyType.IsValueType)
            return false;
        var info = new NullabilityInfoContext().Create(prop);
        return info.ReadState == NullabilityState.Nullable;
    }

    private static void Describe(JsonObject schema, Type type, string? member = null)
    {
        var key = member is null ? $"T:{type.FullName}" : $"P:{type.FullName}.{member}";
        if (Docs.Value.TryGetValue(key, out var text))
            schema["description"] = text;
        else if (member is null && type.GetCustomAttributes<RuleRefAttribute>().FirstOrDefault() is { } rule)
            schema["description"] = $"CR {rule.Rule}";
    }

    /// <summary>The XML documentation file the build writes next to the assembly, if it is there.</summary>
    private static IReadOnlyDictionary<string, string> LoadDocs()
    {
        var path = Path.ChangeExtension(typeof(VocabularySchema).Assembly.Location, ".xml");
        if (!File.Exists(path))
            return new Dictionary<string, string>();
        var doc = XDocument.Load(path);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in doc.Descendants("member"))
        {
            var name = member.Attribute("name")?.Value;
            var summary = member.Element("summary");
            if (name is null || summary is null)
                continue;
            var text = string.Join(' ', Render(summary).Split((char[])['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries));
            if (text.Length > 0)
                result[name] = text;
        }

        return result;
    }

    /// <summary>
    /// A doc comment as the translator should read it: the words, with a <c>paramref</c> or a
    /// <c>see</c> rendered as the name it points at. <c>XElement.Value</c> drops those tags, and
    /// with them a word from every comment that names a parameter or a record.
    /// </summary>
    private static string Render(XElement element)
    {
        var parts = new System.Text.StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    parts.Append(text.Value);
                    break;
                case XElement child when child.Name.LocalName is "paramref" or "typeparamref":
                    parts.Append(child.Attribute("name")?.Value);
                    break;
                case XElement child when child.Name.LocalName == "see":
                    parts.Append(child.Attribute("langword")?.Value ?? LastSegment(child.Attribute("cref")?.Value));
                    break;
                case XElement child:
                    parts.Append(Render(child));
                    break;
                default:
                    break;
            }
        }

        return parts.ToString();
    }

    /// <summary>"T:MtgEngine.Cards.DealDamage" reads as "DealDamage"; a member reference keeps its member name.</summary>
    private static string LastSegment(string? cref)
    {
        if (string.IsNullOrEmpty(cref))
            return string.Empty;
        var body = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var paren = body.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
            body = body[..paren];
        var dot = body.LastIndexOf('.');
        return dot >= 0 ? body[(dot + 1)..] : body;
    }
}
