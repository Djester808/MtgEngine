using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Content files from the API project land next to the test assembly, so the rules
/// document is where <see cref="ComprehensiveRules"/> looks first and this only has to
/// satisfy the constructor.
/// </summary>
internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "MtgEngine.Api.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } =
        new PhysicalFileProvider(AppContext.BaseDirectory);
}

/// <summary>
/// Parses the rules document that actually ships, not a fixture.
/// </summary>
/// <remarks>
/// A fixture would prove the parser handles the shapes we thought of. The published file
/// is 9,400 lines of a format Wizards controls, so what needs proving is that it handles
/// the shapes <em>they</em> used — the subrule with a stray trailing dot (119.1d.), the
/// rule missing its dot entirely (606.5), the two-letter subrule (704.5aa), the indented
/// continuation paragraphs. The parser throws on any line it does not recognise, so
/// "it parsed" is itself the strongest assertion here; the counts below are what stops a
/// future release from parsing to a fraction of the document without anyone noticing.
/// </remarks>
public sealed class ComprehensiveRulesTests
{
    private static readonly ParsedRulesFacade Rules = ParsedRulesFacade.Load();

    // ---- The document parses, whole ----

    [Fact]
    public void Parses_all_nine_sections()
    {
        Assert.Equal(9, Rules.Index.Sections.Length);
        Assert.Equal(
            new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 },
            Rules.Index.Sections.Select(s => s.Number).ToArray());
        Assert.Equal("Game Concepts", Rules.Index.Sections[0].Title);
        Assert.Equal("Casual Variants", Rules.Index.Sections[8].Title);
    }

    [Fact]
    public void Parses_the_whole_rulebook_not_a_prefix_of_it()
    {
        // Floors, not exact counts: a rules release adds rules and must not fail the
        // suite, but parsing that silently collapses to a handful of groups must.
        Assert.True(Rules.Meta.GroupCount > 140, $"only {Rules.Meta.GroupCount} rule groups");
        Assert.True(Rules.Meta.RuleCount > 3000, $"only {Rules.Meta.RuleCount} rules");
        Assert.True(Rules.Meta.GlossaryCount > 700, $"only {Rules.Meta.GlossaryCount} glossary entries");
    }

    [Fact]
    public void Reads_the_effective_date_off_the_document()
    {
        Assert.DoesNotContain("unknown", Rules.Meta.EffectiveDate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("20", Rules.Meta.EffectiveDate, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_group_belongs_to_the_section_it_is_numbered_under()
    {
        foreach (var section in Rules.Index.Sections)
        {
            foreach (var group in section.Groups)
                Assert.Equal(section.Number, group.Number / 100);
        }
    }

    // ---- Rule shapes the published file actually contains ----

    [Fact]
    public void Attaches_subrules_to_their_parent_rule()
    {
        var flying = Rules.GetRule("702.9");

        Assert.Equal("Flying", flying.Text);
        Assert.NotEmpty(flying.Subrules);
        Assert.Contains(flying.Subrules, s => s.Number == "702.9a");
        Assert.Contains(flying.Subrules, s => s.Text.Contains("evasion ability", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_a_subrule_whose_number_carries_a_stray_trailing_dot()
    {
        // The document prints "119.1d." where every sibling prints "119.1c".
        var rule = Rules.GetRule("119.1d");
        Assert.StartsWith("In a two-player Brawl game", rule.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_a_rule_that_is_missing_its_trailing_dot()
    {
        // 606.5 is printed without the dot that 606.4 and 606.6 both have.
        var rule = Rules.GetRule("606.5");
        Assert.Contains("loyalty", rule.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reads_a_two_letter_subrule()
    {
        var rule = Rules.GetRule("704.5aa");
        Assert.NotEmpty(rule.Text);
    }

    [Fact]
    public void Accepts_a_rule_number_written_with_a_trailing_dot()
    {
        Assert.Equal(Rules.GetRule("702.9").Number, Rules.GetRule("702.9.").Number);
    }

    [Fact]
    public void Keeps_the_worked_examples_the_document_prints()
    {
        var withExamples = Rules.AllRules().Where(r => r.Examples.Length > 0).ToArray();

        Assert.True(withExamples.Length > 200, $"only {withExamples.Length} rules kept examples");
        Assert.All(withExamples, r => Assert.All(r.Examples, e =>
        {
            Assert.NotEmpty(e);
            // The "Example:" label is the delimiter, not part of the example.
            Assert.DoesNotContain("Example:", e, StringComparison.Ordinal);
        }));
    }

    [Fact]
    public void Unknown_rules_and_groups_are_not_found_rather_than_null()
    {
        Assert.Throws<ResourceNotFoundException>(() => Rules.GetRule("999.99"));
        Assert.Throws<ResourceNotFoundException>(() => Rules.Service.GetGroup(999));
        Assert.Throws<ResourceNotFoundException>(() => Rules.Service.GetKeyword("Nonesuch"));
    }

    [Fact]
    public void A_miss_does_not_echo_what_the_caller_asked_for()
    {
        // These two take their key straight off the route, and the handler puts a
        // ResourceNotFoundException's Message into the ProblemDetails Detail verbatim.
        const string Injected = "<script>alert('xss')</script>";

        var keyword = Assert.Throws<ResourceNotFoundException>(() => Rules.Service.GetKeyword(Injected));
        var rule = Assert.Throws<ResourceNotFoundException>(() => Rules.GetRule(Injected));

        Assert.DoesNotContain("script", keyword.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("script", rule.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Keywords: all of them, in the three kinds the rules define ----

    [Fact]
    public void Collects_every_keyword_ability_keyword_action_and_ability_word()
    {
        var byCategory = Rules.Service.Keywords
            .GroupBy(k => k.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.True(byCategory["Keyword Ability"] > 190, $"only {byCategory["Keyword Ability"]} keyword abilities");
        Assert.True(byCategory["Keyword Action"] > 60, $"only {byCategory["Keyword Action"]} keyword actions");
        Assert.True(byCategory["Ability Word"] > 55, $"only {byCategory["Ability Word"]} ability words");
    }

    [Theory]
    // The sixteen the old hardcoded knowledge base knew about...
    [InlineData("Flying", "Keyword Ability")]
    [InlineData("Deathtouch", "Keyword Ability")]
    [InlineData("Double Strike", "Keyword Ability")]
    [InlineData("Ward", "Keyword Ability")]
    // ...and the ones it did not.
    [InlineData("Cascade", "Keyword Ability")]
    [InlineData("Cumulative Upkeep", "Keyword Ability")]
    [InlineData("For Mirrodin!", "Keyword Ability")]
    [InlineData("Start Your Engines!", "Keyword Ability")]
    [InlineData("Scry", "Keyword Action")]
    [InlineData("Proliferate", "Keyword Action")]
    [InlineData("Venture into the Dungeon", "Keyword Action")]
    [InlineData("Landfall", "Ability Word")]
    [InlineData("Metalcraft", "Ability Word")]
    [InlineData("Will Of The Council", "Ability Word")]
    public void Knows_the_keyword(string name, string category)
    {
        var keyword = Rules.Service.GetKeyword(name);

        Assert.Equal(category, keyword.Category);
        Assert.NotEmpty(keyword.Definition);
        Assert.NotEmpty(keyword.RuleRef);
        Assert.NotEmpty(keyword.Rules);
    }

    [Fact]
    public void Keyword_lookup_ignores_case_so_a_link_from_card_text_resolves()
    {
        Assert.Equal("Flying", Rules.Service.GetKeyword("flying").Name);
        Assert.Equal("First Strike", Rules.Service.GetKeyword("FIRST STRIKE").Name);
    }

    [Fact]
    public void Keyword_definitions_come_from_the_glossary_not_from_us()
    {
        var deathtouch = Rules.Service.GetKeyword("Deathtouch");
        Assert.Contains("keyword ability", deathtouch.Definition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_keyword_carries_an_implementation_status()
    {
        // The point of the rework: this is a reference to the game's rules, not a report
        // on what some other component supports. Nothing here should read like a badge.
        var suspect = new[] { "implemented", "stub", "not yet enforced", "Phase 1", "skeleton" };

        foreach (var keyword in Rules.Service.Keywords)
        {
            foreach (var word in suspect)
                Assert.DoesNotContain(word, keyword.Definition, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- The card-text link index ----

    [Fact]
    public void Offers_link_targets_for_keyword_abilities_and_ability_words()
    {
        var matches = Rules.Service.KeywordLinks.Select(l => l.Match).ToArray();

        Assert.Contains("Flying", matches);
        Assert.Contains("Double Strike", matches);
        Assert.Contains("Cascade", matches);
        Assert.Contains("Landfall", matches);
        Assert.True(matches.Length > 240, $"only {matches.Length} linkable terms");
    }

    [Fact]
    public void Does_not_link_keyword_actions_because_they_are_ordinary_verbs()
    {
        var matches = Rules.Service.KeywordLinks
            .Select(l => l.Match)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Every one of these appears in the rules text of thousands of cards as a plain
        // verb. Linking them would leave a card with more link than sentence.
        foreach (var verb in new[] { "Destroy", "Exile", "Counter", "Play", "Sacrifice", "Tap", "Search", "Create" })
            Assert.DoesNotContain(verb, matches);
    }

    [Fact]
    public void Links_longest_first_so_a_compound_keyword_wins_over_its_parts()
    {
        var order = Rules.Service.KeywordLinks.Select(l => l.Match).ToList();

        Assert.True(
            order.IndexOf("First Strike") < order.IndexOf("Flash"),
            "longer keywords must be offered before shorter ones they contain");
        Assert.True(
            order.IndexOf("Flashback") < order.IndexOf("Flash"),
            "'Flashback' must be matched before 'Flash'");
    }

    [Fact]
    public void Splits_a_rule_that_names_two_keywords_into_both_spellings()
    {
        var links = Rules.Service.KeywordLinks;

        Assert.Contains(links, l => l.Match == "Daybound" && l.Keyword == "Daybound and Nightbound");
        Assert.Contains(links, l => l.Match == "Nightbound" && l.Keyword == "Daybound and Nightbound");
    }

    [Fact]
    public void Every_link_resolves_to_a_keyword_that_exists()
    {
        foreach (var link in Rules.Service.KeywordLinks)
            Assert.NotEmpty(Rules.Service.GetKeyword(link.Keyword).Name);
    }

    // ---- Glossary ----

    [Fact]
    public void Glossary_pages_and_filters()
    {
        var all = Rules.Service.GetGlossary(new GlossaryRequest { Page = 1, PageSize = 10 });
        Assert.Equal(10, all.Entries.Length);
        Assert.True(all.Total > 700);

        var filtered = Rules.Service.GetGlossary(new GlossaryRequest { Q = "deathtouch", PageSize = 50 });
        Assert.NotEmpty(filtered.Entries);
        Assert.Contains(filtered.Entries, e => e.Term.Equals("Deathtouch", StringComparison.Ordinal));
    }

    [Fact]
    public void Glossary_paging_does_not_repeat_entries_across_pages()
    {
        var first = Rules.Service.GetGlossary(new GlossaryRequest { Page = 1, PageSize = 25 });
        var second = Rules.Service.GetGlossary(new GlossaryRequest { Page = 2, PageSize = 25 });

        Assert.Empty(first.Entries.Select(e => e.Term).Intersect(second.Entries.Select(e => e.Term)));
    }

    // ---- Search ----

    [Fact]
    public void Search_puts_an_exact_rule_number_first()
    {
        var result = Rules.Service.Search(new RulesSearchRequest { Q = "702.9" });

        Assert.Equal("rule", result.Hits[0].Kind);
        Assert.Equal("702.9", result.Hits[0].Ref);
    }

    [Fact]
    public void Search_puts_an_exact_keyword_name_above_the_rules_that_mention_it()
    {
        var result = Rules.Service.Search(new RulesSearchRequest { Q = "Deathtouch" });

        Assert.Equal("keyword", result.Hits[0].Kind);
        Assert.Equal("Deathtouch", result.Hits[0].Ref);
    }

    [Fact]
    public void Search_finds_a_phrase_in_rule_bodies()
    {
        var result = Rules.Service.Search(new RulesSearchRequest { Q = "legend rule", PageSize = 100 });

        Assert.True(result.Total > 0);
        Assert.Contains(result.Hits, h => h.Kind == "rule");
    }

    [Fact]
    public void Search_snippets_show_the_match()
    {
        var result = Rules.Service.Search(new RulesSearchRequest { Q = "summoning sickness", PageSize = 20 });

        Assert.All(result.Hits, h => Assert.NotEmpty(h.Snippet));
        Assert.Contains(
            result.Hits,
            h => h.Snippet.Contains("summoning sickness", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Search_respects_the_page_size_it_is_given()
    {
        var result = Rules.Service.Search(new RulesSearchRequest { Q = "creature", PageSize = 5 });

        Assert.Equal(5, result.Hits.Length);
        Assert.True(result.Total > 5);
    }

    [Fact]
    public void Search_past_the_end_returns_nothing_rather_than_throwing()
    {
        var result = Rules.Service.Search(new RulesSearchRequest { Q = "deathtouch", Page = 900, PageSize = 100 });
        Assert.Empty(result.Hits);
    }

    /// <summary>
    /// Loads the real service once for the whole class. Parsing the document is ~9,400
    /// lines of regex work; doing it per test would dominate the suite's runtime.
    /// </summary>
    private sealed class ParsedRulesFacade
    {
        public required IComprehensiveRules Service { get; init; }

        public RulesMetaDto Meta => Service.Meta;
        public RulesIndexDto Index => Service.Index;

        public RuleDto GetRule(string number) => Service.GetRule(number);

        public IEnumerable<RuleDto> AllRules()
        {
            foreach (var section in Index.Sections)
            {
                foreach (var summary in section.Groups)
                {
                    foreach (var rule in Service.GetGroup(summary.Number).Rules)
                    {
                        yield return rule;
                        foreach (var sub in rule.Subrules)
                            yield return sub;
                    }
                }
            }
        }

        public static ParsedRulesFacade Load() => new()
        {
            Service = new ComprehensiveRules(new TestHostEnvironment(), new NullLogger<ComprehensiveRules>()),
        };
    }
}
