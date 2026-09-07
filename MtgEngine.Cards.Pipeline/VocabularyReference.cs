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
        OneSpelling(sb);

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

    /// <summary>
    /// The same rules <see cref="Agree.Canonical"/> applies after the fact, said once to the model
    /// so it does not have to be applied: two readings that differ only in whether a default was
    /// written out are not a disagreement, and every one of them cost a card its status in the
    /// 200-face pilot. Stated for the model here and enforced in code there; if you change one,
    /// change the other.
    /// </summary>
    private static void OneSpelling(StringBuilder sb)
    {
        sb.AppendLine("## One spelling");
        sb.AppendLine("Where a value is the default, omit it. Do not write it out:");
        sb.AppendLine("- who?: omit it when the player is you, the controller of the ability. \"Draw a card\" is {\"kind\":\"Draw\",\"n\":{\"kind\":\"Fixed\",\"n\":1}} — no \"who\". Name a player only when the card names one.");
        sb.AppendLine("- controlledBy?: omit it inside a cost. \"Sacrifice a creature\" already means one you control.");
        sb.AppendLine("- in?: omit \"Battlefield\" on a filter describing permanents; a filter with no zone means the battlefield. Name a zone only when the card means another one (\"a creature card from your graveyard\").");
        sb.AppendLine("- in?: omit it on the \"for\" filter of a Search; the search's own \"from\" already says the zone.");
        sb.AppendLine("- Keys (\"key\", \"storeAs\", \"storeChoiceAs\", \"rememberAs\") are free text; any name reads the same. Use short ones and reuse the same name for the same thing within a face.");
        sb.AppendLine("- Use GrantKeyword for a keyword, never GrantAbility wrapping a KeywordAbility. \"Gains flying until end of turn\" is GrantKeyword.");
        sb.AppendLine("- One instruction is that instruction, not a Sequence of one step. Omit an empty \"of\" or \"except\" rather than writing {}.");
        sb.AppendLine("- n?/count?: omit a count of one wherever the record allows it. \"Discard a card\" is {\"kind\":\"Discard\"}; \"target creature\" is a Target with no \"count\". Write the count only from two upward, or when it is not a fixed number.");
        sb.AppendLine("- ownedBy?: omit it on the \"for\" filter of a Search of a library. You search your own library, so \"a card you own\" there says nothing. On a graveyard or exile filter the owner matters — keep it.");
        sb.AppendLine();
        sb.AppendLine("Two more, where the same instruction has two shapes rather than an extra word:");
        sb.AppendLine("- Basic lands carry the Land type. \"Basic land card\" is {\"supertypes\":[\"Basic\"],\"types\":[\"Land\"]} — write both, never Basic alone.");
        sb.AppendLine("- A search that shuffles uses Search's own \"shuffleAfter\": true. Do not write Search with \"shuffleAfter\": false followed by a Shuffle step; that is the same instruction spelled twice.");
        sb.AppendLine("- Remember a target only when a later step needs it after the object may have moved. \"Exile target creature, then its controller draws\" reads the target again; \"exile target creature, then return it\" remembers it, because the card it returns is no longer the object that was targeted.");
        sb.AppendLine("- \"Any target\" is one selector: {\"kind\":\"AnyTarget\"}. \"Deals 3 damage to any target\" is {\"kind\":\"DealDamage\",\"n\":{\"kind\":\"Fixed\",\"n\":3},\"to\":{\"kind\":\"AnyTarget\"}} — no \"toPlayer\". Never a Target whose filter names Creature, Planeswalker and Battle, and never an open filter, beside a \"toPlayer\". Use \"to\" and \"toPlayer\" together only when the card names two different things to damage.");
        sb.AppendLine("- \"Can't lose the game\", \"can't win the game\" and \"can't gain life\" have records of their own — CantLoseGame, CantWinGame, CantGainLife — each naming the player. Use PlayerCant only for the restrictions those three do not cover: LifeTotalChanges (\"your life total can't change\", which is more than not gaining), BeTargeted, BeDealtDamage.");
        sb.AppendLine();
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
