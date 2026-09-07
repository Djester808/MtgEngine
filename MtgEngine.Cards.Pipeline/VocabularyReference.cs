using System.Reflection;
using System.Text;

namespace MtgEngine.Cards.Pipeline;

/// <summary>
/// A compact, human-readable listing of the vocabulary for the translator's prompt: one line
/// per record with its parameters and types, grouped by base, with the doc comment where
/// there is one. A fraction of the size of the JSON Schema and easier for a model to read,
/// while the schema stays the thing we validate against.
/// </summary>
public static class VocabularyReference
{
    public static string Text()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Card vocabulary");
        sb.AppendLine("Every value below is a JSON object with a \"kind\" property naming the record, plus the parameters listed.");
        sb.AppendLine("Parameters marked ? are optional and may be omitted. Lists are JSON arrays. Flags (Color, CardType, Supertype) are arrays of names.");
        sb.AppendLine();

        Section(sb, "CardDefinition (the root)", [typeof(CardDefinition), typeof(Face), typeof(TypeLine), typeof(PrintedStat), typeof(TokenSpec), typeof(EmblemSpec), typeof(ClassLevel), typeof(LevelBand), typeof(Mode), typeof(CounterSpec), typeof(ManaProduction), typeof(Comparison), typeof(ObjectFilter), typeof(PlayerFilter)]);

        foreach (var baseType in CardFileSerializer.PolymorphicBases)
        {
            if (baseType == typeof(Provenance) || baseType == typeof(ScenarioSource) || baseType == typeof(ScenarioStep) || baseType == typeof(Expectation))
                continue;
            var derived = CardFileSerializer.RegisteredNames(baseType).Keys.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
            Section(sb, $"{baseType.Name} kinds", derived, baseType);
        }

        Enums(sb);
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, IReadOnlyList<Type> types, Type? baseType = null)
    {
        sb.Append("## ").AppendLine(title);
        if (baseType is not null && Doc(baseType) is { } baseDoc)
            sb.AppendLine(baseDoc);
        foreach (var type in types)
        {
            sb.Append("- ").Append(type.Name).Append('(').Append(Parameters(type)).Append(')');
            if (Doc(type) is { } doc)
                sb.Append("  — ").Append(doc);
            sb.AppendLine();
        }

        sb.AppendLine();
    }

    private static string Parameters(Type type)
    {
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        var parts = new List<string>();
        if (ctor is not null)
        {
            foreach (var p in ctor.GetParameters())
            {
                var optional = p.HasDefaultValue || Nullable.GetUnderlyingType(p.ParameterType) is not null || IsNullableRef(p);
                parts.Add($"{Camel(p.Name!)}{(optional ? "?" : string.Empty)}: {TypeName(p.ParameterType)}");
            }
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name == "EqualityContract" || !prop.CanWrite)
                continue;
            if (ctor?.GetParameters().Any(p => p.Name == prop.Name) == true)
                continue;
            parts.Add($"{Camel(prop.Name)}?: {TypeName(prop.PropertyType)}");
        }

        return string.Join(", ", parts);
    }

    private static bool IsNullableRef(ParameterInfo p) =>
        !p.ParameterType.IsValueType && new NullabilityInfoContext().Create(p).ReadState == NullabilityState.Nullable;

    private static string TypeName(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return TypeName(underlying);
        if (type == typeof(string))
            return "string";
        if (type == typeof(int) || type == typeof(long))
            return "int";
        if (type == typeof(bool))
            return "bool";
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return TypeName(type.GetGenericArguments()[0]) + "[]";
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
            return "{string: " + TypeName(type.GetGenericArguments()[1]) + "}";
        return type.Name;
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static void Enums(StringBuilder sb)
    {
        sb.AppendLine("## Enums");
        foreach (var e in new[] { typeof(Zone), typeof(CardType), typeof(Supertype), typeof(Color), typeof(Step), typeof(Phase), typeof(Layout), typeof(FaceRole), typeof(Placement), typeof(ComparisonOp), typeof(Timing), typeof(TriggerRole), typeof(DoubleWhat), typeof(ExchangeWhat), typeof(ChooseWhat), typeof(DiscardChooser), typeof(AbilityKind) })
            sb.Append("- ").Append(e.Name).Append(": ").AppendLine(string.Join(", ", Enum.GetNames(e)));
        sb.Append("- KeywordKind: ").AppendLine(string.Join(", ", Enum.GetNames<KeywordKind>()));
        sb.AppendLine("- Counters are strings such as \"+1/+1\", \"-1/-1\", \"loyalty\", \"charge\", \"poison\", \"energy\", \"time\", \"stun\", \"shield\", \"oil\".");
        sb.AppendLine();
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Docs = new(() =>
    {
        var path = Path.ChangeExtension(typeof(CardDefinition).Assembly.Location, ".xml");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;
        foreach (var member in System.Xml.Linq.XDocument.Load(path).Descendants("member"))
        {
            var name = member.Attribute("name")?.Value;
            var summary = member.Element("summary");
            if (name is null || summary is null)
                continue;
            // <see cref="T:MtgEngine.Cards.Also"/> reads as its last segment, so the sentence keeps its noun.
            var text = string.Concat(summary.DescendantNodes().Select(n => n switch
            {
                System.Xml.Linq.XText t => t.Value,
                System.Xml.Linq.XElement e when e.Name == "see" || e.Name == "paramref" =>
                    (e.Attribute("cref")?.Value ?? e.Attribute("name")?.Value ?? string.Empty).Split('.').Last(),
                _ => string.Empty,
            }));
            result[name] = string.Join(' ', text.Split((char[])['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries));
        }

        return result;
    });

    private static string? Doc(Type type) => Docs.Value.TryGetValue($"T:{type.FullName}", out var d) ? d : null;
}
