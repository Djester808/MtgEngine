using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MtgEngine.Api.Dtos;

namespace MtgEngine.Api.Services;

/// <summary>
/// The Magic Comprehensive Rules, parsed once from the published document and served as
/// structured data.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a hand-written knowledge base that existed to describe a rules engine:
/// every entry carried an "implemented / partial / stub" badge, the mechanics section
/// documented C# types (<c>ManaPool</c>, <c>StateBasedActions.Apply()</c>, "Phase 1"), and
/// the keyword list was the sixteen members of a <c>KeywordAbility</c> enum rather than
/// the game's keywords. None of that survives here. The document is the content, and the
/// document is complete: all nine sections, every numbered rule and subrule, every worked
/// example, every keyword ability, keyword action and ability word, and the full glossary.
/// </para>
/// <para>
/// It loads from <c>Knowledge/comprehensive-rules.txt</c> for the same reason the
/// deckbuilding doctrine does: moving to a new rules release is dropping in a new file,
/// not editing C#. Download the current text from Magic.Wizards.com/Rules, replace the
/// file, and the parsed shape follows.
/// </para>
/// </remarks>
public interface IComprehensiveRules
{
    /// <summary>Provenance and totals for the loaded document.</summary>
    RulesMetaDto Meta { get; }

    /// <summary>Sections, their rule groups, and every keyword — everything the navigation needs.</summary>
    RulesIndexDto Index { get; }

    /// <summary>Every keyword with its definition, for the keyword list.</summary>
    IReadOnlyList<KeywordDto> Keywords { get; }

    /// <summary>Literal strings worth linking inside card text, longest first.</summary>
    IReadOnlyList<KeywordLinkDto> KeywordLinks { get; }

    /// <summary>A rule group with its full text, e.g. 702 for the keyword abilities.</summary>
    RuleGroupDto GetGroup(int number);

    /// <summary>One numbered rule or subrule, e.g. "702.9" or "509.1a".</summary>
    RuleDto GetRule(string number);

    /// <summary>A keyword with the rules that define it.</summary>
    KeywordDetailDto GetKeyword(string name);

    /// <summary>The glossary, optionally filtered, always paged.</summary>
    GlossaryResultDto GetGlossary(GlossaryRequest request);

    /// <summary>Full-text search across rules, keywords, and the glossary.</summary>
    RulesSearchResultDto Search(RulesSearchRequest request);
}

public sealed class ComprehensiveRules : IComprehensiveRules
{
    public const string FileName = "comprehensive-rules.txt";
    public const string FolderName = "Knowledge";

    private readonly Dictionary<int, RuleGroupDto> _groups;
    private readonly Dictionary<string, RuleDto> _rulesByNumber;
    private readonly Dictionary<string, KeywordDetailDto> _keywords = new(StringComparer.OrdinalIgnoreCase);
    private readonly GlossaryEntryDto[] _glossary;
    private readonly SearchEntry[] _searchIndex;
    private readonly ILogger<ComprehensiveRules> _logger;

    public RulesMetaDto Meta { get; }
    public RulesIndexDto Index { get; }
    public IReadOnlyList<KeywordDto> Keywords { get; }
    public IReadOnlyList<KeywordLinkDto> KeywordLinks { get; }

    public ComprehensiveRules(IHostEnvironment env, ILogger<ComprehensiveRules> logger)
    {
        _logger = logger;

        var path = Path.Combine(AppContext.BaseDirectory, FolderName, FileName);

        // Fall back to the source tree so `dotnet run` works before a copy-to-output.
        if (!File.Exists(path))
            path = Path.Combine(env.ContentRootPath, FolderName, FileName);

        if (!File.Exists(path))
        {
            // Loud, not silent. A knowledge base that quietly serves an empty rulebook is
            // a working page with nothing in it, which is the hardest failure to notice
            // from the outside.
            throw new InvalidOperationException(
                $"Comprehensive Rules not found at '{path}'. It ships as a content file; " +
                $"check that {FolderName}/{FileName} exists and that the csproj copies it to output.");
        }

        var parsed = ComprehensiveRulesParser.Parse(File.ReadAllText(path));

        _groups = parsed.Groups.ToDictionary(g => g.Number);
        _rulesByNumber = parsed.RulesByNumber;
        _glossary = parsed.Glossary;
        KeywordLinks = parsed.KeywordLinks;

        foreach (var keyword in parsed.Keywords)
            _keywords.TryAdd(keyword.Name, keyword);

        Meta = new RulesMetaDto(
            parsed.Title,
            parsed.EffectiveDate,
            parsed.Sections.Length,
            parsed.Groups.Length,
            _rulesByNumber.Count,
            parsed.Keywords.Length,
            _glossary.Length);

        Keywords = [.. parsed.Keywords.Select(k => new KeywordDto(k.Name, k.Category, k.RuleRef, k.Definition))];

        Index = new RulesIndexDto(
            Meta,
            parsed.Sections,
            [.. parsed.Keywords.Select(k => new KeywordSummaryDto(k.Name, k.Category, k.RuleRef))]);

        _searchIndex = BuildSearchIndex(parsed);

        logger.LogInformation(
            "Comprehensive Rules loaded ({Effective}): {Sections} sections, {Groups} groups, " +
            "{Rules} rules, {Keywords} keywords, {Glossary} glossary entries, from {Path}",
            Meta.EffectiveDate, Meta.SectionCount, Meta.GroupCount, Meta.RuleCount,
            Meta.KeywordCount, Meta.GlossaryCount, path);
    }

    public RuleGroupDto GetGroup(int number) =>
        _groups.TryGetValue(number, out var group)
            ? group
            : throw new ResourceNotFoundException($"Rule group {number} was not found.");

    // The two lookups below take their key straight off the route, so the miss message is
    // a fixed string and the value that missed is logged instead. `Rule group 999 was not
    // found` is safe because the route constrains it to an int; `keywords/{name}` accepts
    // anything a URL can carry, and echoing that back into ProblemDetails is the "never
    // return an exception's raw Message when it can contain request content" case.

    public RuleDto GetRule(string number)
    {
        if (_rulesByNumber.TryGetValue(number.Trim().TrimEnd('.'), out var rule))
            return rule;

        _logger.LogInformation("Rules lookup missed for rule {Number}", number);
        throw new ResourceNotFoundException("That rule number is not in the rules document.");
    }

    public KeywordDetailDto GetKeyword(string name)
    {
        if (_keywords.TryGetValue(name.Trim(), out var keyword))
            return keyword;

        _logger.LogInformation("Rules lookup missed for keyword {Name}", name);
        throw new ResourceNotFoundException("That keyword is not in the rules document.");
    }

    public GlossaryResultDto GetGlossary(GlossaryRequest request)
    {
        var q = request.Q?.Trim();

        GlossaryEntryDto[] matches = string.IsNullOrEmpty(q)
            ? _glossary
            : [.. _glossary.Where(e =>
                e.Term.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Definition.Contains(q, StringComparison.OrdinalIgnoreCase))];

        var skip = (request.Page - 1) * request.PageSize;
        return new GlossaryResultDto(
            matches.Length,
            request.Page,
            request.PageSize,
            [.. matches.Skip(skip).Take(request.PageSize)]);
    }

    public RulesSearchResultDto Search(RulesSearchRequest request)
    {
        var q = request.Q.Trim();

        // Rank before paging: typing a bare rule number should put that rule first, even
        // though the same digits appear inside a hundred cross-references.
        var hits = _searchIndex
            .Select(e => (Entry: e, Score: e.Score(q)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Entry.KindWeight)
            .ThenBy(x => x.Entry.SortKey, StringComparer.Ordinal)
            .ToArray();

        var skip = (request.Page - 1) * request.PageSize;
        return new RulesSearchResultDto(
            q,
            hits.Length,
            request.Page,
            request.PageSize,
            [.. hits.Skip(skip).Take(request.PageSize).Select(x => x.Entry.ToHit(q))]);
    }

    private static SearchEntry[] BuildSearchIndex(ParsedRules parsed)
    {
        var entries = new List<SearchEntry>(
            parsed.RulesByNumber.Count + parsed.Keywords.Length + parsed.Glossary.Length);

        foreach (var group in parsed.Groups)
        {
            var title = $"{group.Number}. {group.Title}";
            foreach (var rule in group.Rules)
            {
                entries.Add(new SearchEntry("rule", rule.Number, title, rule.Text));
                foreach (var sub in rule.Subrules)
                    entries.Add(new SearchEntry("rule", sub.Number, title, sub.Text));
            }
        }

        foreach (var keyword in parsed.Keywords)
            entries.Add(new SearchEntry("keyword", keyword.Name, keyword.Name, keyword.Definition));

        foreach (var entry in parsed.Glossary)
            entries.Add(new SearchEntry("glossary", entry.Term, entry.Term, entry.Definition));

        return [.. entries];
    }

    /// <summary>
    /// One searchable row. Scoring is deliberately coarse — exact reference, then title
    /// prefix, then title, then body — because the distinction that matters in a rules
    /// lookup is "you typed a reference" versus "you typed a phrase", not relevance
    /// shading between two paragraphs that both contain the word.
    /// </summary>
    private sealed record SearchEntry(string Kind, string Ref, string Title, string Text)
    {
        public string SortKey => $"{Kind}:{Ref}";

        /// <summary>
        /// Breaks a scoring tie. Searching "deathtouch" matches the keyword and the
        /// glossary term equally well, and the keyword is the better answer: it carries
        /// the rules that define the word, where the glossary carries one sentence that
        /// points at them.
        /// </summary>
        public int KindWeight => Kind switch
        {
            "keyword" => 3,
            "rule" => 2,
            _ => 1,
        };

        public int Score(string q)
        {
            if (Ref.Equals(q, StringComparison.OrdinalIgnoreCase))
                return 100;
            if (Title.Equals(q, StringComparison.OrdinalIgnoreCase))
                return 90;
            if (Ref.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                return 70;
            if (Title.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                return 60;
            if (Title.Contains(q, StringComparison.OrdinalIgnoreCase))
                return 40;
            if (Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                return 20;
            return 0;
        }

        public RulesSearchHitDto ToHit(string q) => new(Kind, Ref, Title, Snippet(q));

        /// <summary>A window around the first match, so a hit shows why it is a hit.</summary>
        private string Snippet(string q)
        {
            const int Window = 90;

            var at = Text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return Text.Length <= Window * 2 ? Text : Text[..(Window * 2)] + "…";

            var start = Math.Max(0, at - Window);
            var end = Math.Min(Text.Length, at + q.Length + Window);
            return (start > 0 ? "…" : "") + Text[start..end] + (end < Text.Length ? "…" : "");
        }
    }
}

// ---- Parser -------------------------------------------------
//
// The published document is plain text with a strict, stable grammar. Every line in the
// rules body is one of: a section header ("5. Turn Structure"), a rule group header
// ("509. Declare Blockers Step"), a numbered rule ("509.1."), a lettered subrule
// ("509.1a"), a worked example ("Example: ..."), an indented continuation paragraph, or
// blank. The glossary that follows is blank-line-separated blocks whose first line is the
// term. Nothing below is heuristic — the grammar accounts for every line in the file, and
// an unrecognised line throws rather than being dropped, so a future rules release that
// changes the format fails at startup instead of silently serving a partial rulebook.

internal sealed record ParsedRules(
    string Title,
    string EffectiveDate,
    RuleSectionDto[] Sections,
    RuleGroupDto[] Groups,
    Dictionary<string, RuleDto> RulesByNumber,
    KeywordDetailDto[] Keywords,
    KeywordLinkDto[] KeywordLinks,
    GlossaryEntryDto[] Glossary);

internal static partial class ComprehensiveRulesParser
{
    internal const string KeywordAbility = "Keyword Ability";
    internal const string KeywordAction = "Keyword Action";
    internal const string AbilityWord = "Ability Word";

    private const int KeywordAbilitiesGroup = 702;
    private const int KeywordActionsGroup = 701;
    private const string AbilityWordsRule = "207.2c";

    /// <summary>
    /// Keywords whose names are not what a card actually prints, so linking them inside
    /// card text would only produce noise. "Landwalk" is the rules' name for a family (a
    /// card says "islandwalk"); "∞" is a symbol, not a word; "Visit" would match inside
    /// the unrelated "Roll to Visit Your Attractions".
    /// </summary>
    private static readonly HashSet<string> NotLinkable =
        new(StringComparer.OrdinalIgnoreCase) { "Landwalk", "∞ (Infinity)", "Visit" };

    /// <summary>
    /// Rules that name two keywords in one heading. Cards print them separately, so the
    /// link index needs both spellings pointing at the one entry.
    /// </summary>
    private static readonly Dictionary<string, string[]> SplitLinkNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Daybound and Nightbound"] = ["Daybound", "Nightbound"],
        };

    [GeneratedRegex(@"^([1-9])\.\s+(\S.*)$")]
    private static partial Regex SectionHeader();

    [GeneratedRegex(@"^(\d{3})\.\s+(?!\d)(\S.*)$")]
    private static partial Regex GroupHeader();

    [GeneratedRegex(@"^(\d{3}\.\d+[a-z]{1,2})\.?\s+(\S.*)$")]
    private static partial Regex Subrule();

    [GeneratedRegex(@"^(\d{3}\.\d+)\.?\s+(\S.*)$")]
    private static partial Regex TopRule();

    [GeneratedRegex(@"^Example:\s*(\S.*)$")]
    private static partial Regex Example();

    [GeneratedRegex(@"effective as of ([^.]+)\.")]
    private static partial Regex EffectiveDateLine();

    [GeneratedRegex(@"The ability words are (.+?)\.\s*$")]
    private static partial Regex AbilityWordList();

    public static ParsedRules Parse(string document)
    {
        var lines = document
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var (bodyStart, glossaryStart, creditsStart) = FindBoundaries(lines);

        var groups = ParseBody(lines, bodyStart, glossaryStart);
        var glossary = ParseGlossary(lines, glossaryStart, creditsStart);

        var rulesByNumber = new Dictionary<string, RuleDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            foreach (var rule in group.Rules)
            {
                rulesByNumber[rule.Number] = rule;
                foreach (var sub in rule.Subrules)
                    rulesByNumber[sub.Number] = sub;
            }
        }

        var sections = groups
            .GroupBy(g => (g.SectionNumber, g.SectionTitle))
            .OrderBy(g => g.Key.SectionNumber)
            .Select(g => new RuleSectionDto(
                g.Key.SectionNumber,
                g.Key.SectionTitle,
                [.. g.Select(x => new RuleGroupSummaryDto(x.Number, x.Title, x.Rules.Length))]))
            .ToArray();

        var keywords = BuildKeywords(groups, rulesByNumber, glossary);

        return new ParsedRules(
            Title: lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim()
                   ?? "Magic: The Gathering Comprehensive Rules",
            EffectiveDate: ReadEffectiveDate(lines, bodyStart),
            Sections: sections,
            Groups: groups,
            RulesByNumber: rulesByNumber,
            Keywords: keywords,
            KeywordLinks: BuildKeywordLinks(keywords),
            Glossary: glossary);
    }

    /// <summary>
    /// Locates the rules body, the glossary, and the credits.
    /// </summary>
    /// <remarks>
    /// The document opens with a table of contents that repeats every section and group
    /// heading, so headings alone cannot say where the body starts. What separates the
    /// two is that only the body contains numbered rules: the first "100.1"-shaped line is
    /// inside the body, and its section header is the nearest one above it.
    /// </remarks>
    private static (int Body, int Glossary, int Credits) FindBoundaries(string[] lines)
    {
        var firstRule = Array.FindIndex(lines, l => TopRule().IsMatch(l) || Subrule().IsMatch(l));
        if (firstRule < 0)
        {
            throw new InvalidOperationException(
                "No numbered rules found; the rules document is not in the expected format.");
        }

        var body = firstRule;
        for (var i = firstRule; i >= 0; i--)
        {
            if (SectionHeader().IsMatch(lines[i]))
            {
                body = i;
                break;
            }
        }

        var glossary = Array.FindLastIndex(lines, l => l.Trim().Equals("Glossary", StringComparison.Ordinal));
        var credits = Array.FindLastIndex(lines, l => l.Trim().Equals("Credits", StringComparison.Ordinal));

        if (glossary <= body)
            throw new InvalidOperationException("No glossary found after the rules body.");

        if (credits <= glossary)
            credits = lines.Length;

        return (body, glossary, credits);
    }

    private static string ReadEffectiveDate(string[] lines, int bodyStart)
    {
        for (var i = 0; i < bodyStart; i++)
        {
            var match = EffectiveDateLine().Match(lines[i]);
            if (match.Success)
                return match.Groups[1].Value.Trim();
        }

        return "unknown";
    }

    private static RuleGroupDto[] ParseBody(string[] lines, int start, int end)
    {
        var groups = new List<RuleGroupDto>();

        var sectionNumber = 0;
        var sectionTitle = string.Empty;
        var groupNumber = 0;
        var groupTitle = string.Empty;
        var groupRules = new List<RuleBuilder>();

        RuleBuilder? topRule = null;   // where the next subrule attaches
        RuleBuilder? active = null;    // where an example or continuation attaches

        void FlushGroup()
        {
            if (groupNumber != 0)
            {
                groups.Add(new RuleGroupDto(
                    groupNumber,
                    groupTitle,
                    sectionNumber,
                    sectionTitle,
                    [.. groupRules.Select(r => r.Build())]));
            }

            groupRules = [];
            topRule = null;
            active = null;
        }

        for (var i = start; i < end; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // A continuation paragraph of whatever came last — the document indents these
            // rather than giving them a number of their own.
            if (char.IsWhiteSpace(line[0]))
            {
                active?.Continue(line.Trim());
                continue;
            }

            var section = SectionHeader().Match(line);
            if (section.Success)
            {
                FlushGroup();
                groupNumber = 0;
                sectionNumber = int.Parse(section.Groups[1].Value, CultureInfo.InvariantCulture);
                sectionTitle = section.Groups[2].Value.Trim();
                continue;
            }

            var group = GroupHeader().Match(line);
            if (group.Success)
            {
                FlushGroup();
                groupNumber = int.Parse(group.Groups[1].Value, CultureInfo.InvariantCulture);
                groupTitle = group.Groups[2].Value.Trim();
                continue;
            }

            // Subrules are tested first: "509.1a" would otherwise read as rule "509.1".
            var subrule = Subrule().Match(line);
            if (subrule.Success && topRule is not null)
            {
                active = topRule.AddSubrule(subrule.Groups[1].Value, subrule.Groups[2].Value.Trim());
                continue;
            }

            var rule = TopRule().Match(line);
            if (rule.Success)
            {
                topRule = new RuleBuilder(rule.Groups[1].Value, rule.Groups[2].Value.Trim());
                active = topRule;
                groupRules.Add(topRule);
                continue;
            }

            var example = Example().Match(line);
            if (example.Success)
            {
                active?.AddExample(example.Groups[1].Value.Trim());
                continue;
            }

            throw new InvalidOperationException(
                $"Unrecognised line {i + 1} in the rules body: '{Truncate(line)}'. " +
                "The published grammar changed; update ComprehensiveRulesParser.");
        }

        FlushGroup();
        return [.. groups];
    }

    private static GlossaryEntryDto[] ParseGlossary(string[] lines, int start, int end)
    {
        var entries = new List<GlossaryEntryDto>();
        var block = new List<string>();

        void Flush()
        {
            // A term with no definition beneath it is a stray line, not an entry.
            if (block.Count >= 2)
                entries.Add(new GlossaryEntryDto(block[0].Trim(), string.Join("\n", block.Skip(1)).Trim()));

            block.Clear();
        }

        for (var i = start + 1; i < end; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                Flush();
            else
                block.Add(lines[i]);
        }

        Flush();
        return [.. entries];
    }

    private static KeywordDetailDto[] BuildKeywords(
        RuleGroupDto[] groups,
        Dictionary<string, RuleDto> rulesByNumber,
        GlossaryEntryDto[] glossary)
    {
        var definitions = glossary
            .GroupBy(e => e.Term, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Definition, StringComparer.OrdinalIgnoreCase);

        var keywords = new List<KeywordDetailDto>();
        keywords.AddRange(FromGroup(groups, KeywordAbilitiesGroup, KeywordAbility, definitions));
        keywords.AddRange(FromGroup(groups, KeywordActionsGroup, KeywordAction, definitions));
        keywords.AddRange(FromAbilityWords(rulesByNumber, definitions));

        return [.. keywords.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Turns rule group 701 or 702 into keyword entries. Each keyword is one numbered rule
    /// whose text is nothing but the keyword's name; the subrules beneath it are the
    /// definition. Rule x.1 is the group's prose preamble, not a keyword.
    /// </summary>
    private static IEnumerable<KeywordDetailDto> FromGroup(
        RuleGroupDto[] groups,
        int groupNumber,
        string category,
        Dictionary<string, string> definitions)
    {
        var group = Array.Find(groups, g => g.Number == groupNumber);
        if (group is null)
            yield break;

        foreach (var rule in group.Rules)
        {
            if (rule.Number.Equals($"{groupNumber}.1", StringComparison.Ordinal))
                continue;

            var name = rule.Text.Trim();
            yield return new KeywordDetailDto(name, category, rule.Number, Define(name, definitions, rule), [rule]);
        }
    }

    /// <summary>
    /// Ability words come from the prose of 207.2c, which lists them inline and states
    /// that they have no rules meaning and no entries of their own. That statement is the
    /// definition, so all of them share it unless the glossary happens to carry the term.
    /// </summary>
    private static IEnumerable<KeywordDetailDto> FromAbilityWords(
        Dictionary<string, RuleDto> rulesByNumber,
        Dictionary<string, string> definitions)
    {
        if (!rulesByNumber.TryGetValue(AbilityWordsRule, out var rule))
            yield break;

        var match = AbilityWordList().Match(rule.Text);
        if (!match.Success)
            yield break;

        var names = match.Groups[1].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.StartsWith("and ", StringComparison.OrdinalIgnoreCase) ? n[4..].Trim() : n)
            .Where(n => n.Length > 0);

        foreach (var name in names)
        {
            var display = TitleCase(name);
            yield return new KeywordDetailDto(
                display,
                AbilityWord,
                AbilityWordsRule,
                definitions.TryGetValue(display, out var glossed)
                    ? glossed
                    : "An italicized word with no rules meaning that ties together abilities on " +
                      "different cards that have similar functionality. See rule 207.2c.",
                [rule]);
        }
    }

    private static string Define(string name, Dictionary<string, string> definitions, RuleDto rule)
    {
        if (definitions.TryGetValue(name, out var glossed))
            return glossed;

        // Some newly printed keywords have no glossary entry yet. The first subrule is the
        // document's own opening statement about the keyword, which is the next best line.
        return rule.Subrules.Length > 0 ? rule.Subrules[0].Text : rule.Text;
    }

    /// <summary>
    /// The strings worth matching inside card text. Keyword actions are excluded on
    /// purpose: they are ordinary verbs — destroy, exile, tap, counter, play — and linking
    /// every one of them would turn a card's rules text into a page of links with no
    /// reading left in it. Keyword abilities and ability words are printed as terms, which
    /// is exactly what a reader wants to look up.
    /// </summary>
    private static KeywordLinkDto[] BuildKeywordLinks(KeywordDetailDto[] keywords)
    {
        var links = new List<KeywordLinkDto>();

        foreach (var keyword in keywords)
        {
            if (keyword.Category is not (KeywordAbility or AbilityWord))
                continue;

            if (NotLinkable.Contains(keyword.Name))
                continue;

            if (SplitLinkNames.TryGetValue(keyword.Name, out var split))
                links.AddRange(split.Select(s => new KeywordLinkDto(s, keyword.Name)));
            else
                links.Add(new KeywordLinkDto(keyword.Name, keyword.Name));
        }

        // Longest first, so "First Strike" wins over "Strike" and "Flashback" over "Flash".
        return [.. links
            .OrderByDescending(l => l.Match.Length)
            .ThenBy(l => l.Match, StringComparer.Ordinal)];
    }

    private static string TitleCase(string value) =>
        string.Join(' ', value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static string Truncate(string value) =>
        value.Length <= 80 ? value : value[..80] + "…";

    /// <summary>Mutable while a rule is read across several lines; frozen into a DTO after.</summary>
    private sealed class RuleBuilder(string number, string text)
    {
        private readonly StringBuilder _text = new(text);
        private readonly List<string> _examples = [];
        private readonly List<RuleBuilder> _subrules = [];
        private bool _lastWasExample;

        public string Number { get; } = number;

        public RuleBuilder AddSubrule(string subNumber, string subText)
        {
            var sub = new RuleBuilder(subNumber, subText);
            _subrules.Add(sub);
            return sub;
        }

        public void AddExample(string example)
        {
            _examples.Add(example);
            _lastWasExample = true;
        }

        /// <summary>An indented paragraph belongs to whatever was last read.</summary>
        public void Continue(string paragraph)
        {
            if (_lastWasExample)
                _examples[^1] = $"{_examples[^1]}\n\n{paragraph}";
            else
                _text.Append("\n\n").Append(paragraph);
        }

        public RuleDto Build() => new(
            Number,
            _text.ToString(),
            [.. _examples],
            [.. _subrules.Select(s => s.Build())]);
    }
}
