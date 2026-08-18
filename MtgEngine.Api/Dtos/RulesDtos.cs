using System.ComponentModel.DataAnnotations;

namespace MtgEngine.Api.Dtos;

// ---- Rules knowledge base ----------------------------------
//
// These shapes describe the Comprehensive Rules as published, nothing else. An earlier
// version of this surface carried an implementation status per entry ("implemented" /
// "partial" / "stub") because the knowledge base was a window onto a rules engine that
// was being built alongside it. There is no such engine now, and a reference that tells
// the reader which rules some other component supports is worse than no reference at
// all -- it dates instantly and it answers a question nobody browsing the rules is
// asking. What a rule says is the whole content.

/// <summary>Provenance of the loaded rules document, so the client can show what it is reading.</summary>
public sealed record RulesMetaDto(
    string Title,
    string EffectiveDate,
    int SectionCount,
    int GroupCount,
    int RuleCount,
    int KeywordCount,
    int GlossaryCount);

/// <summary>One of the nine top-level divisions, e.g. "5. Turn Structure".</summary>
public sealed record RuleSectionDto(int Number, string Title, RuleGroupSummaryDto[] Groups);

/// <summary>A numbered rule group, e.g. "702. Keyword Abilities". Index entry only -- no text.</summary>
public sealed record RuleGroupSummaryDto(int Number, string Title, int RuleCount);

/// <summary>
/// A single numbered rule and everything hanging off it: its lettered subrules and any
/// worked examples the document prints beneath it.
/// </summary>
public sealed record RuleDto(string Number, string Text, string[] Examples, RuleDto[] Subrules);

/// <summary>A rule group with its full text, which is what the detail pane renders.</summary>
public sealed record RuleGroupDto(
    int Number,
    string Title,
    int SectionNumber,
    string SectionTitle,
    RuleDto[] Rules);

/// <summary>
/// A keyword ability (CR 702), keyword action (CR 701), or ability word (CR 207.2c).
/// <paramref name="Category"/> is the distinction; the client groups on it.
/// </summary>
public sealed record KeywordDto(
    string Name,
    string Category,
    string RuleRef,
    string Definition);

/// <summary>A keyword plus the rules that define it, so the detail pane needs one call.</summary>
public sealed record KeywordDetailDto(
    string Name,
    string Category,
    string RuleRef,
    string Definition,
    RuleDto[] Rules);

/// <summary>A glossary term as the document defines it.</summary>
public sealed record GlossaryEntryDto(string Term, string Definition);

/// <summary>
/// One literal string worth turning into a link inside card text, and the keyword it
/// resolves to. The two differ where a rule names two keywords at once ("Daybound and
/// Nightbound"), and the policy for which keywords are safe to match at all lives on the
/// server so the client does not have to re-derive it.
/// </summary>
public sealed record KeywordLinkDto(string Match, string Keyword);

/// <summary>
/// A keyword as the navigation lists it. The definition is deliberately absent: carrying
/// one for all 324 keywords put the index at 81 KB, and the sidebar only ever renders the
/// name. The detail pane fetches the definition with the rules, in one call, when a
/// keyword is actually opened.
/// </summary>
public sealed record KeywordSummaryDto(string Name, string Category, string RuleRef);

/// <summary>Everything the knowledge base needs to draw its navigation in one request.</summary>
public sealed record RulesIndexDto(
    RulesMetaDto Meta,
    RuleSectionDto[] Sections,
    KeywordSummaryDto[] Keywords);

/// <summary>A single search hit. <paramref name="Kind"/> is "rule", "keyword", or "glossary".</summary>
public sealed record RulesSearchHitDto(string Kind, string Ref, string Title, string Snippet);

public sealed record RulesSearchResultDto(
    string Query,
    int Total,
    int Page,
    int PageSize,
    RulesSearchHitDto[] Hits);

/// <summary>
/// Query for <c>GET /api/rules/search</c>. MVC validates this before the service runs, so
/// the service never sees an unbounded page size or a megabyte of query text.
/// </summary>
public sealed record RulesSearchRequest
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    public string Q { get; init; } = string.Empty;

    [Range(1, 1000)]
    public int Page { get; init; } = 1;

    // Small by default: the results list is read on a phone first.
    [Range(1, 100)]
    public int PageSize { get; init; } = 25;
}

/// <summary>Query for <c>GET /api/rules/glossary</c>: an optional filter over a long list.</summary>
public sealed record GlossaryRequest
{
    [StringLength(100)]
    public string? Q { get; init; }

    [Range(1, 1000)]
    public int Page { get; init; } = 1;

    [Range(1, 100)]
    public int PageSize { get; init; } = 50;
}

public sealed record GlossaryResultDto(int Total, int Page, int PageSize, GlossaryEntryDto[] Entries);
