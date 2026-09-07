using System.Reflection;
using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 101.4: the game stops on a question and resumes by branching on its kind, so a kind that
/// is spelled one way where it is raised and another way where it is routed is a question that
/// is asked and never answered — the game would wait for ever. Every kind therefore has a
/// constant in <see cref="ChoiceKinds"/>, and this file is the gate: it sweeps the engine's own
/// source for a kind spelled as a literal, and checks that a question of each kind still goes
/// where it went.
/// </summary>
public sealed partial class ChoiceResumptionTests
{
    /// <summary>
    /// The tokens that make a statement a choice statement. A string literal only counts as a
    /// kind when it sits in a statement that raises, carries or branches on one; a prompt or an
    /// option id in the same statement is excluded by <see cref="LooksLikeAKind"/>, which admits
    /// only the lower-case, hyphenated shape every kind is written in.
    /// </summary>
    private static readonly string[] ChoiceTokens =
        ["PendingChoice", "new Resumption(", ".Kind", "Kind:", "Ask(", "Look("];

    /// <summary>
    /// Call heads whose string argument is a dictionary key or an option id and never a kind —
    /// a context key such as the legend rule's card name, or an answer id such as "yes".
    /// </summary>
    private static readonly string[] LookupSlots =
        ["Add(", "SetItem(", "TryGetValue(", "ContainsKey(", "GetValueOrDefault(", "Remove(", "ChoiceOption(", "Prefer("];

    /// <summary>
    /// The sweeper itself, checked against a snippet written here rather than against the tree,
    /// so this test says the same thing after another wave converts the interpreter's literals
    /// to constants: it must find the kind in a raise, ignore the prompt beside it, ignore the
    /// option ids and the context key, and follow a local <c>const string</c> that is used as a
    /// kind (which is how a kind hides from a plain grep).
    /// </summary>
    [Fact]
    [RuleRef("101.4")]
    public void The_kind_sweeper_finds_a_literal_kind_and_not_a_prompt_or_an_option_id()
    {
        const string source = """
            private const string Elsewhere = "far-away";
            private const string Reason = "destroyed";

            public Outcome Ask(Run run)
            {
                Emit(new ObjectDestroyed(id, Reason));
                return Ask(run, who, "made-up", "Choose one.", [new ChoiceOption("yes", "Yes")], 1, 1);
            }

            private PendingChoice Question(GameState state) =>
                new(id, player, Elsewhere, "Pick a card.", options, 1, 1,
                    new ChoiceContext(null, null, Elsewhere, Empty.Add("name", name)));

            private static bool Ours(Resumption resumption) =>
                string.Equals(resumption.Kind, "gone-astray", StringComparison.Ordinal);
            """;

        var found = KindsIn(source);

        Assert.Contains("made-up", found);
        Assert.Contains("far-away", found);
        Assert.Contains("gone-astray", found);
        Assert.DoesNotContain("yes", found);
        Assert.DoesNotContain("name", found);
        Assert.DoesNotContain("destroyed", found);
    }

    /// <summary>
    /// CR 101.4: every kind the engine raises anywhere is a declared constant. The sweep reads
    /// the rules project's own source — the engine, not the tests — so a new question raised with
    /// a literal fails here rather than at a table, where it would stop the game with a kind
    /// nothing routes. A literal that spells a declared constant's value passes: the subsystems
    /// swap their literals for the constants in their own waves, and a kind is either declared or
    /// it is not, whichever spelling reaches it first.
    /// </summary>
    [Fact]
    [RuleRef("101.4")]
    public void Every_choice_kind_spelled_in_the_rules_engine_is_a_declared_constant()
    {
        var declared = DeclaredKinds();
        var offenders = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        var files = RulesSources().ToList();
        Assert.True(files.Count > 20, $"the rules project's source was not found to sweep ({files.Count} files)");

        foreach (var file in files)
        {
            foreach (var kind in KindsIn(File.ReadAllText(file)))
            {
                if (declared.Contains(kind))
                    continue;
                if (!offenders.TryGetValue(kind, out var where))
                    offenders[kind] = where = new SortedSet<string>(StringComparer.Ordinal);
                where.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "these choice kinds are spelled as literals and have no constant in ChoiceKinds: "
                + string.Join("; ", offenders.Select(o => $"\"{o.Key}\" in {string.Join(", ", o.Value)}")));
    }

    /// <summary>
    /// CR 101.4 with 608.2: the kinds a rule of the game raised are finished by the choice system,
    /// and every other kind is a card's question and goes back to the interpreter carrying the same
    /// kind it was asked with. The routing branches on the constants, so this is the behaviour a
    /// re-spelled literal would break.
    /// </summary>
    [Theory]
    [InlineData(ChoiceKinds.May)]
    [InlineData(ChoiceKinds.Unless)]
    [InlineData(ChoiceKinds.MayPay)]
    [InlineData(ChoiceKinds.MayCast)]
    [InlineData(ChoiceKinds.CastCost)]
    [InlineData(ChoiceKinds.Value)]
    [InlineData(ChoiceKinds.Mode)]
    [InlineData(ChoiceKinds.Sacrifice)]
    [InlineData(ChoiceKinds.Search)]
    [InlineData(ChoiceKinds.Scry)]
    [InlineData(ChoiceKinds.Surveil)]
    [InlineData(ChoiceKinds.Targets)]
    [InlineData(ChoiceKinds.NewTargets)]
    [InlineData(ChoiceKinds.RingBearer)]
    [InlineData(ChoiceKinds.TriggerOrder)]
    [InlineData(ChoiceKinds.ReplacementOrder)]
    [RuleRef("101.4")]
    public void A_question_a_card_raised_goes_back_to_the_interpreter_by_its_kind(string kind)
    {
        var board = new ChoiceResumptionBoard();
        var card = board.Put("a");
        var choice = board.Ask(kind, ChoiceResumptionBoard.Options(card));

        choices.Answer(board.State, new AnswerChoice(First, choice.Id, ChoiceResumptionBoard.Answer(card)));

        Assert.Equal(kind, Assert.Single(interpreter.Resumed).Kind);
        Assert.Empty(zones.Moves);
    }

    /// <summary>
    /// CR 514.1, 704.5j and 103.5: the three kinds a rule raises are finished here and never
    /// handed on, which is the other half of the routing — a kind that fell through to the
    /// interpreter would resume an effect tree that does not exist.
    /// </summary>
    [Theory]
    [InlineData(ChoiceKinds.Discard)]
    [InlineData(ChoiceKinds.LegendRule)]
    [InlineData(ChoiceKinds.Mulligan)]
    [InlineData(ChoiceKinds.MulliganBottom)]
    [RuleRef("101.4")]
    public void A_question_a_rule_raised_is_finished_by_the_choice_system(string kind)
    {
        var board = new ChoiceResumptionBoard();
        var card = board.Put("a");
        var choice = board.Ask(kind, ChoiceResumptionBoard.Options(card));

        choices.Answer(board.State, new AnswerChoice(First, choice.Id, ChoiceResumptionBoard.Answer(card)));

        Assert.Empty(interpreter.Resumed);
    }

    /// <summary>The values of every constant on <see cref="ChoiceKinds"/>.</summary>
    private static ImmutableHashSet<string> DeclaredKinds() =>
        [.. typeof(ChoiceKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];

    /// <summary>
    /// Every C# file of the rules project except the one that declares the kinds. The project is
    /// found by walking up from the test assembly to the directory holding it, so the sweep reads
    /// the source that was built rather than a copy.
    /// </summary>
    private static IEnumerable<string> RulesSources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MtgEngine.Rules", "MtgEngine.Rules.csproj")))
            dir = dir.Parent;

        if (dir is null)
            return [];

        var project = Path.Combine(dir.FullName, "MtgEngine.Rules");
        return Directory
            .EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetFileName(f), "ChoiceKinds.cs", StringComparison.Ordinal))
            .Where(f => !Built(project, f));
    }

    /// <summary>Whether a file is build output rather than source: <c>bin</c> and <c>obj</c>.</summary>
    private static bool Built(string project, string file)
    {
        var relative = Path.GetRelativePath(project, file);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return string.Equals(first, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, "obj", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The kinds spelled as string literals in one source file. A statement — the text between
    /// braces and semicolons — counts when it holds one of <see cref="ChoiceTokens"/>; inside it,
    /// a kind-shaped literal is a kind unless it sits in a <see cref="LookupSlots"/> argument, and
    /// a <c>const string</c> of the same file named in that statement contributes its value, which
    /// is how a kind declared once and used by name is still seen.
    /// </summary>
    private static IReadOnlyCollection<string> KindsIn(string source)
    {
        var consts = Constants(source);
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var statement in source.Split(['{', '}', ';']))
        {
            if (!ChoiceTokens.Any(t => statement.Contains(t, StringComparison.Ordinal)))
                continue;

            var quoted = Quoted(statement).ToList();
            for (var i = 0; i < quoted.Count; i++)
            {
                var (value, before) = quoted[i];
                if (!LooksLikeAKind(value) || LookupSlots.Any(s => before.EndsWith(s, StringComparison.Ordinal)))
                    continue;

                // Every question is raised with its prompt straight after its kind, and routed by
                // comparing the kind it carries. A kind-shaped literal anywhere else is a verb, an
                // option id or a key — the answer ids "yes" and "no", the reason on a destruction —
                // and naming one a kind would make this sweep fail on code that is already right.
                if (Prompted(quoted, i) || Compared(before))
                    found.Add(value);
            }

            foreach (var (name, value) in consts)
            {
                if (LooksLikeAKind(value) && Names(statement, name))
                    found.Add(value);
            }
        }

        return found;
    }

    /// <summary>The <c>const string</c> declarations of one file, by name.</summary>
    private static IReadOnlyList<(string Name, string Value)> Constants(string source)
    {
        const string Marker = "const string ";
        var declarations = new List<(string, string)>();

        for (var at = source.IndexOf(Marker, StringComparison.Ordinal); at >= 0; at = source.IndexOf(Marker, at + 1, StringComparison.Ordinal))
        {
            var rest = source[(at + Marker.Length)..];
            var equals = rest.IndexOf('=');
            var open = rest.IndexOf('"');
            if (equals < 0 || open < 0 || open < equals)
                continue;
            var close = rest.IndexOf('"', open + 1);
            if (close < 0)
                continue;

            var name = rest[..equals].Trim();
            if (name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_'))
                declarations.Add((name, rest[(open + 1)..close]));
        }

        return declarations;
    }

    /// <summary>
    /// Whether the next literal in the statement is a prompt — a sentence put to a player, which
    /// is what follows a kind at every place a question is raised. A prompt has a space in it; an
    /// option's label, an id and a key do not.
    /// </summary>
    private static bool Prompted(IReadOnlyList<(string Value, string Before)> quoted, int at) =>
        at + 1 < quoted.Count && quoted[at + 1].Value.Contains(' ', StringComparison.Ordinal);

    /// <summary>
    /// Whether a literal is being compared with, or matched against, a kind the game is carrying —
    /// <c>resumption.Kind == "…"</c>, <c>string.Equals(choice.Kind, "…")</c>, <c>{ Kind: "…" }</c>.
    /// That is the routing side of a kind, and a literal there is the spelling that would silently
    /// stop matching if the raising side were ever corrected.
    /// </summary>
    private static bool Compared(string before)
    {
        var text = before.TrimEnd();
        if (text.Length == 0 || !(text.EndsWith("==", StringComparison.Ordinal)
            || text.EndsWith("!=", StringComparison.Ordinal)
            || text[^1] is ',' or ':' or '('))
            return false;

        var window = text.Length <= 48 ? text : text[^48..];

        // "ChoiceKinds." spells the word too, and reading a constant is the opposite of the thing
        // being looked for.
        return window.Replace("ChoiceKinds", string.Empty, StringComparison.Ordinal)
            .Contains("Kind", StringComparison.Ordinal);
    }

    /// <summary>Every plain string literal in one statement, with the text that precedes it.</summary>
    private static IEnumerable<(string Value, string Before)> Quoted(string statement)
    {
        for (var open = statement.IndexOf('"'); open >= 0;)
        {
            var close = statement.IndexOf('"', open + 1);
            if (close < 0)
                yield break;

            yield return (statement[(open + 1)..close], statement[..open]);
            open = statement.IndexOf('"', close + 1);
        }
    }

    /// <summary>Whether a statement uses an identifier, rather than merely containing its letters.</summary>
    private static bool Names(string statement, string identifier)
    {
        for (var at = statement.IndexOf(identifier, StringComparison.Ordinal); at >= 0; at = statement.IndexOf(identifier, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 || !(char.IsLetterOrDigit(statement[at - 1]) || statement[at - 1] is '_' or '.');
            var end = at + identifier.Length;
            var after = end >= statement.Length || !(char.IsLetterOrDigit(statement[end]) || statement[end] == '_');
            if (before && after)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The shape every kind is written in: lower-case words joined by hyphens. A prompt has
    /// spaces and capitals, an option id is a number or a name, and a path key carries a colon,
    /// so none of them is mistaken for a kind.
    /// </summary>
    private static bool LooksLikeAKind(string value)
    {
        if (value.Length == 0 || !char.IsAsciiLetterLower(value[0]) || value[^1] == '-')
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
                continue;
            if (c == '-' && value[i - 1] != '-')
                continue;
            return false;
        }

        return true;
    }
}
