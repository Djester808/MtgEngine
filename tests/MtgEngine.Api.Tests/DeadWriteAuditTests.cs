using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Markers the engine writes that nothing ever reads.
/// </summary>
/// <remarks>
/// Every other instrument here asks a different question and none of them asks this one. Coverage
/// counts lines the compiler <em>read</em>; <c>MechanicCoverageTests</c> proves each line shape is
/// <em>played</em> by a test; the soaks prove a card does not crash; the invariant suite proves a
/// compiled card is well formed. A card can pass all four while the thing it produces is inert.
/// <para>
/// Stun counters were exactly that for the whole life of the feature. "Put a stun counter on it"
/// compiled to an ordinary named counter, 87 corpus cards put one on, every test passed, and the
/// creature untapped on schedule because no untap step, state-based action or ability ever looked
/// for the counter. The write had no reader, and nothing in the project could say so.
/// </para>
/// <para>
/// This asks the question directly, and it asks it of the compiled artefacts rather than of the
/// source, so it cannot be satisfied by a claim. <see cref="IlIndex"/> decodes the IL of every
/// method in <c>MtgEngine.Rules</c> and <c>MtgEngine.Api</c> and records which methods call which
/// — a property getter called by nobody is a field nothing reads, whatever the source looks like.
/// A source-text scan cannot tell a read from a comment, and a runtime scan cannot see a branch
/// that never ran.
/// </para>
/// <para>
/// <b>False positives are real and are named, not suppressed silently.</b> A field read only by a
/// record's generated equality, only by the event serializer, or only by a test is not read by the
/// engine, and each accepted case below says which of those it is and why that is correct.
/// </para>
/// </remarks>
public sealed class DeadWriteAuditTests(ITestOutputHelper output)
{
    /// <summary>
    /// Findings that are understood and accepted, each with the reason it is not a dead write.
    /// </summary>
    /// <remarks>
    /// Written as <c>Type.Property</c>. Adding a line here is a claim that the marker is inert on
    /// purpose; the reason is the evidence for it. Anything not listed fails the build, which is
    /// the whole point — the stun counter got a decade of green suites precisely because nothing
    /// had to justify it.
    /// </remarks>
    private static readonly ImmutableDictionary<string, string> AcceptedDeadWrites =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Counter kinds the corpus puts on permanents that no engine code names.
    /// </summary>
    /// <remarks>
    /// Most counter kinds are card-local bookkeeping — charge, quest, page — and are read back by
    /// the text of the card that put them on, so no engine code should name them. The dangerous
    /// ones are the kinds whose meaning is in the Comprehensive Rules rather than on the card:
    /// those have to be named somewhere in the engine or they do nothing at all.
    /// </remarks>
    private static readonly ImmutableDictionary<string, string> AcceptedUnnamedCounters =
        ImmutableDictionary<string, string>.Empty;

    [Fact]
    public void No_marker_is_written_by_the_engine_and_read_by_nothing()
    {
        var index = IlIndex.Build();
        var findings = new List<Finding>();

        foreach (var type in TypesOf(typeof(CardCompiler).Assembly))
        {
            if (!IsEngineMarker(type))
                continue;

            foreach (var property in type.GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (property.Name is "EqualityContract" || property.GetMethod is not { } getter)
                    continue;

                var readers = index.CallersOf(getter, property).ToList();
                var consuming = readers.Where(r => Role(r, type) is CallerRole.Consuming).ToList();
                if (consuming.Count > 0)
                    continue;

                var writers = index.WritersOf(property).ToList();
                if (writers.Count == 0)
                    continue;

                findings.Add(new Finding(
                    $"{type.Name}.{property.Name}",
                    Describe(property),
                    writers.Select(Where).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                    readers.Select(r => $"{Role(r, type)}:{Where(r)}")
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()));
            }
        }

        findings = [.. findings.OrderBy(f => f.Marker, StringComparer.Ordinal)];

        output.WriteLine($"{findings.Count} markers written with no consuming read");
        foreach (var finding in findings)
        {
            var accepted = AcceptedDeadWrites.TryGetValue(finding.Marker, out var why) ? $"  [accepted: {why}]" : string.Empty;
            output.WriteLine($"  {finding.Marker} : {finding.Type}{accepted}");
            output.WriteLine($"      written by  {string.Join(", ", finding.Writers)}");
            output.WriteLine($"      read by     {(finding.Readers.Count == 0 ? "(nothing at all)" : string.Join(", ", finding.Readers))}");
        }

        var unexplained = findings.Where(f => !AcceptedDeadWrites.ContainsKey(f.Marker)).ToList();

        Assert.True(
            unexplained.Count == 0,
            $"""
             {unexplained.Count} marker(s) are written and read by nothing.

             Each is a dead write: the engine produces it and no rule, step or ability consumes it,
             so every card that produces it is silently incomplete. Either give it a reader, or add
             it to AcceptedDeadWrites with the reason it is inert on purpose.

             {string.Join("\n", unexplained.Select(f => $"  {f.Marker}  ({f.Type}) written by {string.Join(", ", f.Writers)}"))}
             """);
    }

    [Fact]
    public void Every_counter_kind_the_corpus_puts_on_is_named_by_the_engine_or_read_by_a_card()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var index = IlIndex.Build();
        var literals = index.Literals;

        var putBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var readBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            var put = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kind in CounterKindsIn(compiled))
                put.Add(kind);

            foreach (var kind in put)
                putBy[kind] = putBy.GetValueOrDefault(kind) + 1;

            var text = card.OracleText ?? string.Empty;
            foreach (var kind in put)
            {
                // The card's own text reading the counter back — "remove a charge counter",
                // "for each quest counter on it". A kind read back by the cards that put it on
                // needs no engine code: the compiler's generic counter reader is its reader.
                if (ReadsCounterBack(text, kind))
                    readBy[kind] = readBy.GetValueOrDefault(kind) + 1;
            }
        }

        var unnamed = putBy
            .Where(k => readBy.GetValueOrDefault(k.Key) == 0)
            .Where(k => !literals.Contains(k.Key))
            .OrderByDescending(k => k.Value)
            .ToList();

        output.WriteLine($"{putBy.Count} distinct counter kinds put on by the corpus");
        foreach (var (kind, cards) in putBy.OrderByDescending(k => k.Value).Take(40))
        {
            var named = literals.Contains(kind) ? "engine-named" : "not named";
            var back = readBy.GetValueOrDefault(kind);
            output.WriteLine($"  {kind,-24} {cards,5} cards   {named,-13} read back by {back} of them");
        }

        var unexplained = unnamed.Where(k => !AcceptedUnnamedCounters.ContainsKey(k.Key)).ToList();

        Assert.True(
            unexplained.Count == 0,
            $"""
             {unexplained.Count} counter kind(s) are put on by cards, named by no engine code, and
             read back by none of the cards that put them on. That is the stun-counter shape: the
             marker goes on and nothing anywhere consumes it.

             {string.Join("\n", unexplained.Select(k => $"  {k.Key}  on {k.Value.ToString(CultureInfo.InvariantCulture)} cards"))}
             """);
    }

    /// <summary>Whether a type is somewhere the engine keeps a marker, rather than a wire shape.</summary>
    /// <remarks>
    /// The engine assembly only. The API's DTOs and the per-player <c>GameView</c> are read by a
    /// JSON serializer through reflection, which no call graph can see, so every one of their
    /// properties looks unread and none of them is: they leave through the wire. Auditing them
    /// here would bury the findings that matter under two hundred that do not.
    /// </remarks>
    private static bool IsEngineMarker(Type type) =>
        type.Namespace is { } space
        && space.StartsWith("MtgEngine.Rules", StringComparison.Ordinal)
        && !space.StartsWith("MtgEngine.Rules.Views", StringComparison.Ordinal)
        && !type.Name.EndsWith("Dto", StringComparison.Ordinal);

    private sealed record Finding(string Marker, string Type, List<string> Writers, List<string> Readers);

    private enum CallerRole
    {
        /// <summary>A real read: some rule, step, ability or view actually looks at it.</summary>
        Consuming,

        /// <summary>The record's own generated equality, copy, printing or deconstruction.</summary>
        Boilerplate,

        /// <summary>Written to and read from the event log, and nowhere else.</summary>
        Serialization,

        /// <summary>Only a test looks at it — which is the test asserting its own fixture.</summary>
        Test,
    }

    private static CallerRole Role(MethodBase caller, Type declaring)
    {
        var owner = Outermost(caller.DeclaringType);

        if (owner is null)
            return CallerRole.Consuming;

        if (owner.Assembly != declaring.Assembly && owner.Assembly.GetName().Name?.EndsWith(".Tests", StringComparison.Ordinal) == true)
            return CallerRole.Test;

        if (owner.Name is "EventLogSerializer")
            return CallerRole.Serialization;

        if (owner != Outermost(declaring))
            return CallerRole.Consuming;

        // Inside the declaring type. Only the generated members are boilerplate; a real method
        // on the same record that reads its own field is a genuine read.
        if (caller.Name is "Equals" or "GetHashCode" or "ToString" or "PrintMembers" or "Deconstruct" or "<Clone>$")
            return CallerRole.Boilerplate;

        // A record's copy constructor takes the record itself and reads every property.
        if (caller is ConstructorInfo copy
            && copy.GetParameters() is [var only]
            && only.ParameterType == declaring)
        {
            return CallerRole.Boilerplate;
        }

        return CallerRole.Consuming;
    }

    private static Type? Outermost(Type? type)
    {
        while (type?.DeclaringType is { } declaring)
            type = declaring;

        return type;
    }

    private static string Where(MethodBase method) =>
        $"{Outermost(method.DeclaringType)?.Name ?? "?"}.{method.Name}";

    private static string Describe(PropertyInfo property)
    {
        var type = property.PropertyType;
        return type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(",", type.GetGenericArguments().Select(a => a.Name))}>"
            : type.Name;
    }

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException loaded)
        {
            return loaded.Types.OfType<Type>();
        }
    }

    /// <summary>Every counter kind this card can put on something, by name.</summary>
    /// <remarks>
    /// Taken from the compiled card rather than its text, so it is the set the engine can actually
    /// produce. Any string-valued property called <c>Kind</c> or <c>CounterKind</c> on an effect
    /// that also carries a count is a counter name — asking the shape rather than keeping a list
    /// of counter effects, so a new one is covered the day it is added.
    /// </remarks>
    private static IEnumerable<string> CounterKindsIn(CompiledCard compiled)
    {
        foreach (var effect in EveryEffect(compiled))
        {
            var type = effect.GetType();
            var counted = type.GetProperties().Any(p =>
                p.PropertyType == typeof(Amount) || p.Name is "Counters" or "Count" or "Delta");

            if (!counted)
                continue;

            foreach (var property in type.GetProperties())
            {
                if (property.PropertyType != typeof(string) || property.Name is not ("Kind" or "CounterKind"))
                    continue;

                if (property.GetValue(effect) is string { Length: > 0 } kind)
                    yield return kind;
            }
        }
    }

    private static IEnumerable<IEffect> EveryEffect(CompiledCard compiled)
    {
        foreach (var list in EffectLists(compiled, 0))
        {
            foreach (var effect in EffectTree.Flatten(list))
                yield return effect;
        }
    }

    /// <summary>Every effect list anywhere in a compiled card, found by walking it.</summary>
    /// <remarks>
    /// Reflective rather than a hand-written tour of <c>Spell</c>, <c>Adventure</c>, the activated
    /// abilities and the triggers, because four separate walkers over a <c>CompiledCard</c> had
    /// each already missed something different — between them leaving every adventure, prepared
    /// half, cleave text, gift and split face checked by nothing at all.
    /// </remarks>
    private static IEnumerable<ImmutableList<IEffect>> EffectLists(object? node, int depth)
    {
        if (node is null || depth > 8)
            yield break;

        if (node is ImmutableList<IEffect> effects)
        {
            yield return effects;
            yield break;
        }

        if (node is string or IEffect)
            yield break;

        if (node is IEnumerable sequence)
        {
            foreach (var item in sequence)
            {
                foreach (var found in EffectLists(item, depth + 1))
                    yield return found;
            }

            yield break;
        }

        var type = node.GetType();
        if (type.Namespace?.StartsWith("MtgEngine.", StringComparison.Ordinal) != true)
            yield break;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            object? value;
            try
            {
                value = property.GetValue(node);
            }
            catch (TargetInvocationException)
            {
                continue;
            }

            foreach (var found in EffectLists(value, depth + 1))
                yield return found;
        }
    }

    /// <summary>Whether a card's own text reads a counter back rather than only putting it on.</summary>
    private static bool ReadsCounterBack(string text, string kind)
    {
        foreach (var verb in new[] { "remove", "for each", "if", "with", "without", "as long as", "that many", "number of" })
        {
            var needle = $"{kind} counter";
            var at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                var window = text[Math.Max(0, at - 60)..at];
                if (window.Contains(verb, StringComparison.OrdinalIgnoreCase)
                    && !window.EndsWith("put a ", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                at = text.IndexOf(needle, at + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    /// <summary>
    /// Who calls whom, and what strings exist, read straight out of the compiled IL.
    /// </summary>
    /// <remarks>
    /// The only honest way to ask "does anything read this". Source text cannot tell a read from a
    /// comment or a doc-comment <c>cref</c>; a running game cannot see a branch it did not take.
    /// The opcode table is built from <see cref="OpCodes"/> by reflection rather than written out,
    /// for the same reason nothing else here keeps a list.
    /// </remarks>
    private sealed class IlIndex
    {
        private readonly Dictionary<MethodBase, List<MethodBase>> _callers = [];

        private IlIndex(IReadOnlyList<Assembly> assemblies) => Assemblies = assemblies;

        public IReadOnlyList<Assembly> Assemblies { get; }

        public HashSet<string> Literals { get; } = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<short, OpCode> ByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => o.Value);

        public static IlIndex Build()
        {
            var index = new IlIndex([typeof(CardCompiler).Assembly, typeof(MtgEngine.Api.Cards.CardPool).Assembly]);

            foreach (var assembly in index.Assemblies)
            {
                foreach (var type in TypesOf(assembly))
                {
                    var members = type
                        .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Cast<MethodBase>()
                        .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));

                    foreach (var member in members)
                        index.Walk(member);
                }
            }

            return index;
        }

        /// <summary>Every method that calls this property's getter, including through a base or interface.</summary>
        public IEnumerable<MethodBase> CallersOf(MethodInfo getter, PropertyInfo property)
        {
            foreach (var accessor in Accessors(getter, property))
            {
                if (_callers.TryGetValue(accessor, out var found))
                {
                    foreach (var caller in found)
                        yield return caller;
                }
            }
        }

        /// <summary>
        /// Every method that writes this property — through its setter, or by constructing the
        /// record that declares it as a positional parameter.
        /// </summary>
        /// <remarks>
        /// A positional record parameter is stored by the constructor with <c>stfld</c>, never
        /// through <c>set_</c>, so a setter search alone reports every positional record as
        /// unwritten. Construction is the write.
        /// </remarks>
        public IEnumerable<MethodBase> WritersOf(PropertyInfo property)
        {
            if (property.SetMethod is { } setter && _callers.TryGetValue(setter, out var setters))
            {
                foreach (var caller in setters)
                    yield return caller;

                yield break;
            }

            var declaring = property.DeclaringType;
            if (declaring is null)
                yield break;

            foreach (var constructor in declaring.GetConstructors())
            {
                if (!constructor.GetParameters().Any(p =>
                        string.Equals(p.Name, property.Name, StringComparison.Ordinal)
                        && p.ParameterType == property.PropertyType))
                {
                    continue;
                }

                if (_callers.TryGetValue(constructor, out var built))
                {
                    foreach (var caller in built)
                        yield return caller;
                }
            }
        }

        private static IEnumerable<MethodBase> Accessors(MethodInfo getter, PropertyInfo property)
        {
            yield return getter;

            var declaring = property.DeclaringType;
            if (declaring is null || declaring.IsInterface)
                yield break;

            foreach (var contract in declaring.GetInterfaces())
            {
                if (contract.GetProperty(property.Name) is { GetMethod: { } theirs })
                    yield return theirs;
            }

            if (getter.GetBaseDefinition() is { } root && root != getter)
                yield return root;
        }

        private void Walk(MethodBase method)
        {
            byte[] il;
            try
            {
                if (method.GetMethodBody()?.GetILAsByteArray() is not { } body)
                    return;

                il = body;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            var typeArguments = method.DeclaringType?.IsGenericType == true
                ? method.DeclaringType.GetGenericArguments()
                : null;
            var methodArguments = method is MethodInfo { IsGenericMethodDefinition: true } generic
                ? generic.GetGenericArguments()
                : null;

            var i = 0;
            while (i < il.Length)
            {
                var first = il[i++];
                var value = first == 0xFE && i < il.Length
                    ? unchecked((short)((0xFE << 8) | il[i++]))
                    : (short)first;

                if (!ByValue.TryGetValue(value, out var opcode))
                    return;

                var operand = i;
                i += OperandSize(opcode, il, operand);
                if (i > il.Length)
                    return;

                switch (opcode.OperandType)
                {
                    case OperandType.InlineMethod or OperandType.InlineTok:
                        Record(method, BitConverter.ToInt32(il, operand), typeArguments, methodArguments);
                        break;

                    case OperandType.InlineString:
                        try
                        {
                            Literals.Add(method.Module.ResolveString(BitConverter.ToInt32(il, operand)));
                        }
                        catch (ArgumentException)
                        {
                            // A token this module cannot resolve is not a literal we can read.
                        }

                        break;

                    default:
                        break;
                }
            }
        }

        private void Record(MethodBase caller, int token, Type[]? typeArguments, Type[]? methodArguments)
        {
            MethodBase? callee;
            try
            {
                callee = caller.Module.ResolveMethod(token, typeArguments, methodArguments);
            }
            catch (ArgumentException)
            {
                return;
            }

            if (callee is null)
                return;

            if (callee is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } closed)
                callee = closed.GetGenericMethodDefinition();

            if (!_callers.TryGetValue(callee, out var list))
                _callers[callee] = list = [];

            list.Add(caller);
        }

        private static int OperandSize(OpCode opcode, byte[] il, int operand) => opcode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
                or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, operand)),
            _ => int.MaxValue,
        };
    }
}
