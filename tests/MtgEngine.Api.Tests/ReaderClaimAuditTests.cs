using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Diagnostic scaffold. Not shipped as-is.
/// </summary>
public sealed class ReaderClaimAuditScratch(ITestOutputHelper output)
{
    internal static IReadOnlyList<(string Owner, string Name, Regex Rx)> AnchoredReaders()
    {
        var found = new List<(string, string, Regex)>();

        foreach (var type in new[] { typeof(CardCompiler), typeof(EffectPhrase) })
        {
            Collect(type, type.Name, found);

            foreach (var nested in type.GetNestedTypes(
                BindingFlags.Public | BindingFlags.NonPublic))
            {
                Collect(nested, type.Name + "." + nested.Name, found);
            }
        }

        return found;
    }

    private static void Collect(Type type, string owner, List<(string, string, Regex)> into)
    {
        foreach (var method in type.GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.ReturnType != typeof(Regex) || method.GetParameters().Length != 0)
                continue;

            if (method.Invoke(null, null) is not Regex rx)
                continue;

            var pattern = rx.ToString();
            if (!pattern.StartsWith('^'))
                continue;

            if (!pattern.EndsWith('$') && !pattern.EndsWith("$)", StringComparison.Ordinal))
                continue;

            into.Add((owner, method.Name, rx));
        }
    }

    [Fact]
    public void Dump_readers()
    {
        var readers = AnchoredReaders();
        output.WriteLine($"anchored readers: {readers.Count}");
        foreach (var (owner, name, rx) in readers.Take(20))
            output.WriteLine($"  {owner}.{name}  {rx}");
    }

    [Fact]
    public void Dump_reader_precision()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus");
            return;
        }

        var readers = AnchoredReaders();
        output.WriteLine($"anchored readers: {readers.Count}");

        var unread = new Dictionary<string, int>(StringComparer.Ordinal);
        var read = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            CompiledCard compiled;
            List<string> lines;
            try
            {
                compiled = CardCompiler.Compile(card);
                lines = CardCompiler.Lines(card).ToList();
            }
            catch (Exception)
            {
                continue;
            }

            var bad = compiled.Unhandled.ToHashSet(StringComparer.Ordinal);

            foreach (var line in lines.Distinct(StringComparer.Ordinal))
            {
                var into = bad.Contains(line) ? unread : read;
                into[line] = into.GetValueOrDefault(line) + 1;
            }
        }

        output.WriteLine($"distinct read lines: {read.Count}  unread: {unread.Count}");

        var stats = new Dictionary<string, (int UnreadCards, int ReadCards, int UnreadLines, List<string> Samples)>(StringComparer.Ordinal);

        foreach (var (bucket, isUnread) in new[] { (unread, true), (read, false) })
        {
            foreach (var (line, count) in bucket)
            {
                foreach (var (owner, name, rx) in readers)
                {
                    bool hit;
                    try
                    {
                        hit = rx.IsMatch(line);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        continue;
                    }

                    if (!hit)
                        continue;

                    var key = owner + "." + name;
                    if (!stats.TryGetValue(key, out var seen))
                        seen = (0, 0, 0, []);

                    if (isUnread)
                    {
                        seen.UnreadCards += count;
                        seen.UnreadLines++;
                        if (seen.Samples.Count < 8)
                            seen.Samples.Add($"[{count}] {line}");
                    }
                    else
                    {
                        seen.ReadCards += count;
                    }

                    stats[key] = seen;
                }
            }
        }

        output.WriteLine("");
        output.WriteLine("=== readers whose matches mostly go UNREAD (candidate claim-and-drop) ===");
        output.WriteLine("   unread/read cards | reader");

        foreach (var (key, seen) in stats
            .Where(p => p.Value.UnreadCards >= 8)
            .OrderByDescending(p => (double)p.Value.UnreadCards / (p.Value.UnreadCards + p.Value.ReadCards))
            .ThenByDescending(p => p.Value.UnreadCards)
            .Take(90))
        {
            var rate = (double)seen.UnreadCards / (seen.UnreadCards + seen.ReadCards);
            output.WriteLine($"{rate,7:P0}  {seen.UnreadCards,6}/{seen.ReadCards,-6} {seen.UnreadLines,5} lines  {key}");
            foreach (var l in seen.Samples.Take(4))
                output.WriteLine($"           {l}");
        }
    }

    /// <summary>Every effect type the compiler contains a `newobj` for.</summary>
    internal static IReadOnlyDictionary<Type, List<string>> BuildableEffects()
    {
        var byValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => o.Value);

        var found = new Dictionary<Type, List<string>>();

        foreach (var type in typeof(CardCompiler).Assembly.GetTypes())
        {
            if (type.Namespace is not { } ns || !ns.StartsWith("MtgEngine.Rules.Cards", StringComparison.Ordinal))
                continue;

            var members = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));

            foreach (var member in members)
                Walk(member, byValue, found);
        }

        return found;
    }

    private static void Walk(
        MethodBase method,
        Dictionary<short, OpCode> byValue,
        Dictionary<Type, List<string>> into)
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

            if (!byValue.TryGetValue(value, out var opcode))
                return;

            var operand = i;
            i += OperandSize(opcode, il, operand);
            if (i > il.Length)
                return;

            if (opcode.OperandType is not (OperandType.InlineMethod or OperandType.InlineTok))
                continue;

            MethodBase? callee;
            try
            {
                callee = method.Module.ResolveMethod(
                    BitConverter.ToInt32(il, operand), typeArguments, methodArguments);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (callee is not ConstructorInfo built || built.DeclaringType is not { } made)
                continue;

            if (made.Namespace is not { } ns
                || !ns.StartsWith("MtgEngine.Rules.Abilities", StringComparison.Ordinal))
            {
                continue;
            }

            if (!into.TryGetValue(made, out var sites))
                into[made] = sites = [];

            var where = (method.DeclaringType?.Name ?? "?") + "." + method.Name;
            if (!sites.Contains(where, StringComparer.Ordinal))
                sites.Add(where);
        }
    }

    private static int OperandSize(OpCode opcode, byte[] il, int operand) =>
        opcode.OperandType switch
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

    /// <summary>Every object of an Abilities type reachable from a compiled card.</summary>
    internal static void Reached(object? node, HashSet<Type> into, HashSet<object> seen, int depth)
    {
        if (node is null || depth > 14)
            return;

        var type = node.GetType();

        if (type.IsPrimitive || node is string || node is Delegate || node is Guid || type.IsEnum)
            return;

        if (node is System.Collections.IEnumerable list and not string)
        {
            foreach (var item in list)
                Reached(item, into, seen, depth + 1);

            return;
        }

        if (!seen.Add(node))
            return;

        if (type.Namespace is { } ns && ns.StartsWith("MtgEngine.Rules.Abilities", StringComparison.Ordinal))
            into.Add(type);

        if (type.Namespace is not { } owner
            || !owner.StartsWith("MtgEngine.Rules", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
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

            Reached(value, into, seen, depth + 1);
        }
    }

    [Fact]
    public void Dump_unreachable_production()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus");
            return;
        }

        var buildable = BuildableEffects();
        output.WriteLine($"ability types the compiler constructs: {buildable.Count}");

        var reached = new HashSet<Type>();

        foreach (var card in corpus)
        {
            CompiledCard compiled;
            try
            {
                compiled = CardCompiler.Compile(card);
            }
            catch (Exception)
            {
                continue;
            }

            Reached(compiled, reached, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        }

        output.WriteLine($"ability types reached over the corpus: {reached.Count}");
        output.WriteLine("");
        output.WriteLine("=== constructed by the compiler, never produced for any corpus card ===");

        foreach (var (type, sites) in buildable
            .Where(p => !reached.Contains(p.Key))
            .OrderBy(p => p.Key.Name, StringComparer.Ordinal))
        {
            output.WriteLine($"  {type.Name}   built at: {string.Join(", ", sites)}");
        }
    }

    private static string Describe(object? node, int depth)
    {
        if (node is null)
            return "null";

        var type = node.GetType();
        if (node is string str)
            return "\"" + str + "\"";

        if (type.IsPrimitive || type.IsEnum || node is Guid)
            return node.ToString() ?? "?";

        if (node is Delegate)
            return "<fn>";

        if (depth > 4)
            return type.Name + "{...}";

        if (node is System.Collections.IEnumerable seq)
        {
            var items = new List<string>();
            foreach (var item in seq)
                items.Add(Describe(item, depth + 1));

            return "[" + string.Join(", ", items) + "]";
        }

        if (type.Namespace is not { } ns || !ns.StartsWith("MtgEngine", StringComparison.Ordinal))
            return type.Name;

        var parts = new List<string>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
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

            if (value is null)
                continue;

            if (value is System.Collections.ICollection { Count: 0 })
                continue;

            if (value is bool b && !b)
                continue;

            if (value is int n && n == 0)
                continue;

            parts.Add(property.Name + "=" + Describe(value, depth + 1));
        }

        return type.Name + "{" + string.Join(", ", parts) + "}";
    }

    internal static CardDefinition Card(string name, string text, string types = "Creature") =>
        new()
        {
            OracleId = "probe-" + name,
            Name = name,
            OracleText = text,
            CardTypes = types switch
            {
                "Enchantment" => MtgEngine.Domain.Enums.CardType.Enchantment,
                "Instant" => MtgEngine.Domain.Enums.CardType.Instant,
                "Sorcery" => MtgEngine.Domain.Enums.CardType.Sorcery,
                "Artifact" => MtgEngine.Domain.Enums.CardType.Artifact,
                "Land" => MtgEngine.Domain.Enums.CardType.Land,
                _ => MtgEngine.Domain.Enums.CardType.Creature,
            },
            Power = 2,
            Toughness = 2,
            ManaCostRaw = "{1}{G}",
        };

    [Fact]
    public void ProbeKeywords()
    {
        var raw = Environment.GetEnvironmentVariable("KW") ?? string.Empty;
        var method = typeof(EffectPhrase).GetMethod(
            "Keywords", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        output.WriteLine("method: " + method);

        var rx = typeof(EffectPhrase).GetMethod(
            "ElidedProtection", BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, null) as Regex;

        output.WriteLine("rx: " + rx);
        output.WriteLine("rewrite: " + rx?.Replace(
            "protection from black and from red", " and protection from "));

        foreach (var words in raw.Split("||", StringSplitOptions.RemoveEmptyEntries))
            output.WriteLine($"{words}  =>  {method?.Invoke(null, [words]) ?? "null"}");
    }

    [Fact]
    public void Probe()
    {
        var raw = Environment.GetEnvironmentVariable("PROBE") ?? string.Empty;
        foreach (var spec in raw.Split("||", StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = spec.Split("::");
            var types = bits.Length > 1 ? bits[0] : "Creature";
            var text = (bits.Length > 1 ? bits[1] : bits[0])
                .Replace("<NL>", "\n", StringComparison.Ordinal);

            var card = Card("Probe Subject", text, types);
            var compiled = CardCompiler.Compile(card);

            output.WriteLine("---- " + types + " :: " + text);
            output.WriteLine("  complete: " + compiled.IsComplete);
            foreach (var line in compiled.Unhandled)
                output.WriteLine("  UNREAD: " + line);

            output.WriteLine("  " + Describe(compiled, 0));
            output.WriteLine("");
        }
    }

    /// <summary>Whether the pattern anchors a whole line rather than a fragment inside one.</summary>
    internal static bool WholeLine(string pattern)
    {
        if (!pattern.StartsWith('^'))
            return false;

        if (!pattern.EndsWith('$') && !pattern.EndsWith("$)", StringComparison.Ordinal))
            return false;

        // "^(a|the) |cards$" starts with a caret and ends with a dollar and anchors neither:
        // the alternation splits it into two fragments. Only a pattern whose caret and dollar
        // are on the same branch claims a line.
        var depth = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == '[')
            {
                while (i < pattern.Length && pattern[i] != ']')
                {
                    if (pattern[i] == '\\')
                        i++;

                    i++;
                }

                continue;
            }

            if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
            else if (c == '|' && depth == 0)
                return false;
        }

        return true;
    }

    [Fact]
    public void Dump_readers_that_read_nothing()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus");
            return;
        }

        var readers = AnchoredReaders().Where(r => WholeLine(r.Rx.ToString())).ToList();
        output.WriteLine($"whole-line readers: {readers.Count}");

        var read = new Dictionary<string, int>(StringComparer.Ordinal);
        var unread = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            CompiledCard compiled;
            List<string> lines;
            try
            {
                compiled = CardCompiler.Compile(card);
                lines = CardCompiler.Lines(card).ToList();
            }
            catch (Exception)
            {
                continue;
            }

            var bad = compiled.Unhandled.ToHashSet(StringComparer.Ordinal);

            foreach (var line in lines.Distinct(StringComparer.Ordinal))
            {
                var into = bad.Contains(line) ? unread : read;
                into[line] = into.GetValueOrDefault(line) + 1;

                foreach (var piece in Pieces(line))
                {
                    if (!string.Equals(piece, line, StringComparison.Ordinal))
                        into[piece] = into.GetValueOrDefault(piece) + 1;
                }
            }
        }

        output.WriteLine($"read fragments: {read.Count}  unread fragments: {unread.Count}");

        var stats = new Dictionary<string, (int Read, int Unread, List<string> Samples)>(StringComparer.Ordinal);

        foreach (var (bucket, isUnread) in new[] { (unread, true), (read, false) })
        {
            foreach (var (line, count) in bucket)
            {
                foreach (var (owner, name, rx) in readers)
                {
                    bool hit;
                    try
                    {
                        hit = rx.IsMatch(line);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        continue;
                    }

                    if (!hit)
                        continue;

                    var key = owner + "." + name;
                    if (!stats.TryGetValue(key, out var seen))
                        seen = (0, 0, []);

                    if (isUnread)
                    {
                        seen.Unread += count;
                        if (seen.Samples.Count < 5)
                            seen.Samples.Add($"[{count}] {line}");
                    }
                    else
                    {
                        seen.Read += count;
                    }

                    stats[key] = seen;
                }
            }
        }

        output.WriteLine("");
        output.WriteLine("=== whole-line readers that match NO line the compiler read ===");
        foreach (var (owner, name, rx) in readers)
        {
            var key = owner + "." + name;
            var seen = stats.GetValueOrDefault(key);
            if (seen.Read > 0)
                continue;

            output.WriteLine($"  {key}   unread matches: {seen.Unread}");
            if (seen.Samples is { Count: > 0 })
            {
                foreach (var sample in seen.Samples.Take(2))
                    output.WriteLine($"        {sample}");
            }

            output.WriteLine($"        /{rx}/");
        }
    }

    /// <summary>A line, and each sentence inside it — what a sentence reader is offered.</summary>
    private static IEnumerable<string> Pieces(string line)
    {
        yield return line;

        IEnumerable<string> sentences;
        try
        {
            sentences = EffectPhrase.SentencesOf(line).ToList();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var sentence in sentences)
        {
            var trimmed = sentence.Trim();
            if (trimmed.Length == 0)
                continue;

            yield return trimmed;
            yield return trimmed.TrimEnd('.');
        }
    }

    [Fact]
    public void Dump_overlaps()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
            return;

        var readers = AnchoredReaders().Where(r => WholeLine(r.Rx.ToString())).ToList();

        var unread = new Dictionary<string, int>(StringComparer.Ordinal);
        var read = new HashSet<string>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            CompiledCard compiled;
            List<string> lines;
            try
            {
                compiled = CardCompiler.Compile(card);
                lines = CardCompiler.Lines(card).ToList();
            }
            catch (Exception)
            {
                continue;
            }

            var bad = compiled.Unhandled.ToHashSet(StringComparer.Ordinal);

            foreach (var line in lines.Distinct(StringComparer.Ordinal))
            {
                foreach (var piece in Pieces(line))
                {
                    if (bad.Contains(line))
                        unread[piece] = unread.GetValueOrDefault(piece) + 1;
                    else
                        read.Add(piece);
                }
            }
        }

        var path = Path.Combine(AppContext.BaseDirectory, "overlaps.txt");
        using var file = new StreamWriter(path);

        foreach (var (line, count) in unread.OrderByDescending(p => p.Value))
        {
            if (read.Contains(line))
                continue;

            var matched = new List<string>();
            foreach (var (owner, name, rx) in readers)
            {
                try
                {
                    if (rx.IsMatch(line))
                        matched.Add(owner + "." + name);
                }
                catch (RegexMatchTimeoutException)
                {
                }
            }

            if (matched.Count == 0)
                continue;

            file.WriteLine($"{count}	{string.Join(",", matched)}	{line}");
        }

        output.WriteLine("wrote " + path);
    }

    [Fact]
    public void Dump_unread_lines()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
            return;

        var unread = new Dictionary<string, int>(StringComparer.Ordinal);
        var oneShort = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            CompiledCard compiled;
            try
            {
                compiled = CardCompiler.Compile(card);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var line in compiled.Unhandled.Distinct(StringComparer.Ordinal))
                unread[line] = unread.GetValueOrDefault(line) + 1;

            if (compiled.Unhandled.Distinct(StringComparer.Ordinal).Count() == 1)
            {
                var only = compiled.Unhandled.Distinct(StringComparer.Ordinal).First();
                oneShort[only] = oneShort.GetValueOrDefault(only) + 1;
            }
        }

        var path = Path.Combine(AppContext.BaseDirectory, "unread.txt");
        using (var file = new StreamWriter(path))
        {
            foreach (var (line, count) in unread.OrderByDescending(p => p.Value))
                file.WriteLine($"{count}	{oneShort.GetValueOrDefault(line)}	{line}");
        }

        output.WriteLine("wrote " + path + "  " + unread.Count);
    }
}
