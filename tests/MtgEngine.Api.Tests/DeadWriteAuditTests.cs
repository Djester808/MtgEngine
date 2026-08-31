using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;
using MtgEngine.Rules.State;
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
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // --- Written to the log, read back out of it by a JSON serializer -------------
            //
            // EventLogSerializer writes events through System.Text.Json, which reads every
            // property by reflection and leaves no call for a call graph to find. So each of
            // these IS persisted and IS replayed; what none of them has is a reducer, rule or
            // view that acts on it. That is a record rather than a defect - but only because
            // somebody checked, one at a time, which is the point of naming them here.
            ["CascadeRequested.SourceId"] =
                "log record; the choice that answers it is found by its own id, not by source",
            ["CombatTax.Id"] =
                "identity only; taxes are gathered by sweeping the battlefield at the moment a "
                + "declaration is made, never looked up by id",
            ["FlashPermission.Id"] =
                "identity only; permissions are gathered by sweeping the battlefield when the "
                + "timing question is asked, never looked up by id",
            ["LibraryTopPermission.Id"] =
                "identity only; gathered by sweeping the battlefield at the moment of the play, "
                + "never looked up by id",
            ["ChoiceMade.ChoiceId"] =
                "log record of which question was answered; the reducer folds the answer",
            ["CommanderDamageDealt.Amount"] =
                "the increment; the reducer folds Total, which is the number CR 903.10a asks for",
            ["CommanderDesignated.CardId"] =
                "log record; the designation the reducer keeps is the oracle id, not the object",
            ["DiscoverRequested.SourceId"] =
                "log record; the discover choice is resumed from the choice id",
            ["FizzledForIllegalTargets.StackId"] =
                "log record of what fizzled; the object is already off the stack when it is written",
            ["ObjectCeasedToExist.From"] =
                "log record; CR 111.7 removes the object wherever it was, so nothing branches on it",
            ["StackObjectResolved.StackId"] =
                "log record; the reducer pops the stack rather than looking the object up",
            ["TriggerRemovedForNoTargets.ControllerId"] =
                "log record of whose trigger it was (CR 603.3d); nothing is owed to them",

            // --- A mirror in state of something the log already says ----------------------
            //
            // Both are real dead writes with no card behind them, and both are the same gap:
            // PlayerViewProjector does not carry them, so a client learns the outcome from the
            // GameEnded and PlayerLost events instead. Worth closing when the board next needs
            // an end-of-game screen; not worth a wire change on its own.
            ["GameState.WinnerId"] =
                "mirror of GameEnded.WinnerId, which the reducer does read; not projected to a view",
            ["PlayerState.LossReason"] =
                "mirror of PlayerLost.Reason; not projected, so the client reads the event",

            // --- An identity nothing looks anything up by ---------------------------------
            //
            // These records all carry a required Id in the house style, and the bans are
            // gathered by sweeping the battlefield rather than by id, so no lookup exists to
            // read one. ContinuousEffectDefinition.Id is the same field on the type where the
            // lookup does exist, which is why the convention is not itself the finding.
            ["ChosenNameBan.Id"] = "identity only; StaticBans are gathered, never looked up by id",
            ["CounterBan.Id"] = "identity only; StaticBans are gathered, never looked up by id",
            ["LifeGainBan.Id"] = "identity only; the ban is cleared wholesale, not by id",
            ["PlayerQualityDefinition.Id"] =
                "identity only; player qualities are applied where they are gathered",
            ["UnpreventableStatic.Id"] =
                "identity only; StaticBans are gathered, never looked up by id",

            // --- Read by a person, not by the engine ---------------------------------------
            ["CardHalf.Name"] =
                "the printed name of a split half; the engine casts a half by index",
            ["CompiledCard.Name"] =
                "diagnostic - every failure message in the compiler suites is built from it",
            ["ConditionalModes.Rule"] = "the CR citation the clause came from, carried for a reader",

            // --- Deliberately recorded and deliberately not acted on ------------------------
            ["CompiledCard.DeckRules"] =
                "CR 903.3a and friends are settled before a game and do nothing during one; "
                    + "the field exists so the fact is not lost, and CompiledCard says so",
            ["DelayedTrigger.TurnCreated"] =
                "Game.FireDelayedTriggers explains it: the ordering makes the arithmetic "
                    + "unnecessary, and a stated duration (CR 603.7b) will need it",
            ["SpellDefinition.FaceDownWard"] =
                "disguise's ward is delivered by the disguise-ward trigger, which carries "
                    + "FunctionsFaceDown (CR 702.168a); this is a second record of the same fact",
        }.ToImmutableDictionary(StringComparer.Ordinal);

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
        var findings = Findings();

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

            // CR 122.1c: a counter whose name is a power and a toughness modifies them by that
            // much, whatever the name is. Asked with the engine's own reader rather than by
            // listing "+1/+1" and its cousins, because that reader is the thing consuming them -
            // "-0/-1" is on three cards, appears as a literal nowhere, and is read by the layers
            // exactly as "+1/+1" is.
            .Where(k => CounterKinds.PowerToughnessOf(k.Key) is null)
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

    /// <summary>Every marker the engine writes and no engine code reads.</summary>
    private static List<Finding> Findings()
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
                if (readers.Any(r => Role(r, type) is CallerRole.Consuming))
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

        return [.. findings.OrderBy(f => f.Marker, StringComparer.Ordinal)];
    }

    /// <summary>
    /// How many real cards produce each dead write, so the list has an order to work down.
    /// </summary>
    /// <remarks>
    /// Asserts nothing - the fact above is the gate. This is the ranking, and it is the number
    /// that decides which finding is worth a day: a marker no card ever sets is a tidy-up, and one
    /// eighty-seven cards set is a feature that has never worked. The stun counter was the latter
    /// and looked like the former from every angle the project could see.
    /// </remarks>
    [Fact]
    public void Dead_writes_are_ranked_by_the_cards_that_produce_them()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present - skipping.");
            return;
        }

        var markers = Findings().Select(f => f.Marker).ToHashSet(StringComparer.Ordinal);
        var cards = new Dictionary<string, int>(StringComparer.Ordinal);
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            var setHere = new HashSet<string>(StringComparer.Ordinal);
            Visit(CardCompiler.Compile(card), 0, [], node =>
            {
                var type = node.GetType();
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var marker = $"{type.Name}.{property.Name}";
                    if (!markers.Contains(marker) || property.GetIndexParameters().Length > 0)
                        continue;

                    if (IsSet(Read(node, property)))
                        setHere.Add(marker);
                }
            });

            foreach (var marker in setHere)
            {
                cards[marker] = cards.GetValueOrDefault(marker) + 1;
                examples.TryAdd(marker, card.Name);
            }
        }

        output.WriteLine($"{markers.Count} markers, of which {cards.Count} are set by at least one real card");
        foreach (var (marker, count) in cards.OrderByDescending(c => c.Value))
            output.WriteLine($"  {count,6} cards  {marker}   e.g. {examples[marker]}");

        foreach (var marker in markers.Where(m => !cards.ContainsKey(m)).Order(StringComparer.Ordinal))
            output.WriteLine($"       0 cards  {marker}   (written during play, not by the compiler)");
    }

    private static object? Read(object node, PropertyInfo property)
    {
        try
        {
            return property.GetValue(node);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static bool IsSet(object? value) => value switch
    {
        null => false,
        string text => text.Length > 0,
        ICollection collection => collection.Count > 0,
        _ => !value.GetType().IsValueType || !value.Equals(Activator.CreateInstance(value.GetType())),
    };

    /// <summary>Every object anywhere inside a compiled card.</summary>
    private static void Visit(object? node, int depth, HashSet<object> seen, Action<object> visit)
    {
        if (node is null || depth > 10 || node is string or Delegate || node.GetType().IsPrimitive)
            return;

        if (node is IEnumerable sequence)
        {
            foreach (var item in sequence)
                Visit(item, depth + 1, seen, visit);

            return;
        }

        if (node.GetType().Namespace?.StartsWith("MtgEngine.", StringComparison.Ordinal) != true)
            return;

        if (!node.GetType().IsValueType && !seen.Add(node))
            return;

        visit(node);

        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0)
                Visit(Read(node, property), depth + 1, seen, visit);
        }
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

        // Its own type first: the serializer declares the wire records it reads back, and
        // rebuilding one of those from the log is the whole point of having written it.
        if (owner != Outermost(declaring))
        {
            return owner.Name is "EventLogSerializer"
                ? CallerRole.Serialization
                : CallerRole.Consuming;
        }

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

            foreach (var property in type.GetProperties())
            {
                if (property.PropertyType != typeof(string) || !NamesACounter(type, property))
                    continue;

                if (property.GetValue(effect) is string { Length: > 0 } kind)
                    yield return kind;
            }
        }
    }

    /// <summary>Whether a string property on an effect holds a counter's name.</summary>
    /// <remarks>
    /// <c>CounterKind</c> says what it is. A bare <c>Kind</c> only names a counter on an effect
    /// that is about counters: <c>Amass</c>'s <c>Kind</c> is the Army's creature type, and taking
    /// it for a counter name filed "Zombies", "Orcs" and "Goblins" as counters that nothing reads
    /// - three findings, fifty-two cards, and not one of them a counter.
    /// </remarks>
    private static bool NamesACounter(Type effect, PropertyInfo property) =>
        property.Name is "CounterKind"
        || (property.Name is "Kind"
            && effect.Name.Contains("Counter", StringComparison.Ordinal));

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
    /// <remarks>
    /// Classified by the <em>putting</em> side rather than by a list of reading words, because the
    /// reading words are open-ended and the putting ones are not: "when there are five or more
    /// plot counters on this enchantment" is a read, and no list of verbs written in advance
    /// contained it.
    /// <para>
    /// <b>Reminder text does not count.</b> A stun counter's reminder — "if a permanent with a
    /// stun counter would become untapped, remove one from it instead" — is the rulebook printed
    /// on the card, not the card reading its own marker, and it appears on 48 of the 54 cards that
    /// put one on. Counting it would have made the counter that started this audit look consumed.
    /// </para>
    /// </remarks>
    private static bool ReadsCounterBack(string text, string kind)
    {
        var plain = WithoutReminderText(text);

        // A card that counts every counter on a permanent reads whatever it put there, whatever
        // the name. Twitching Doll puts a nest counter on itself and then makes a token "for each
        // counter on this creature" - read, by a sentence that never names it.
        foreach (var counted in new[]
        {
            "for each counter", "number of counters", "counters on it", "counters on this",
        })
        {
            if (plain.Contains(counted, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var needle = $"{kind} counter";
        for (var at = plain.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = plain.IndexOf(needle, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var before = plain[Math.Max(0, at - 30)..at];
            if (!before.Contains("put ", StringComparison.OrdinalIgnoreCase)
                && !before.Contains("enters ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A card's text with its parenthesised reminders taken out (CR 207.2).</summary>
    private static string WithoutReminderText(string text)
    {
        var kept = new System.Text.StringBuilder(text.Length);
        var depth = 0;

        foreach (var letter in text)
        {
            if (letter == '(')
                depth++;
            else if (letter == ')' && depth > 0)
                depth--;
            else if (depth == 0)
                kept.Append(letter);
        }

        return kept.ToString();
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
