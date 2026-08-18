using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

/// <summary>
/// The rules knowledge base: the Comprehensive Rules and every keyword, served from the
/// published document.
/// </summary>
/// <remarks>
/// This used to be a hardcoded array in this file describing a rules engine — sixteen
/// keywords tagged "implemented" / "partial" / "stub", plus mechanics entries about
/// <c>ManaPool</c> and "Phase 1". The engine is gone and the reference is not about the
/// engine, so the content is now the rules themselves, parsed by
/// <see cref="IComprehensiveRules"/>. Nothing here holds rules text; the controller binds
/// a request, calls one service, and returns the result.
/// </remarks>
[ApiController]
[AllowAnonymous] // Static reference material behind the public /kb page.
[Route("api/[controller]")]
[ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
public sealed class RulesController(IComprehensiveRules rules) : ControllerBase
{
    /// <summary>Sections, rule groups, and every keyword — enough to draw the navigation.</summary>
    [HttpGet]
    public ActionResult<RulesIndexDto> GetIndex() => Ok(rules.Index);

    /// <summary>Provenance of the loaded document: title, effective date, and totals.</summary>
    [HttpGet("meta")]
    public ActionResult<RulesMetaDto> GetMeta() => Ok(rules.Meta);

    /// <summary>One rule group with its full text, e.g. 702 for the keyword abilities.</summary>
    [HttpGet("groups/{number:int}")]
    public ActionResult<RuleGroupDto> GetGroup(int number) => Ok(rules.GetGroup(number));

    /// <summary>One numbered rule or subrule, e.g. 702.9 or 509.1a.</summary>
    [HttpGet("rules/{number}")]
    public ActionResult<RuleDto> GetRule(string number) => Ok(rules.GetRule(number));

    /// <summary>Every keyword ability, keyword action, and ability word.</summary>
    [HttpGet("keywords")]
    public ActionResult<IReadOnlyList<KeywordDto>> GetKeywords() => Ok(rules.Keywords);

    /// <summary>One keyword with the rules that define it.</summary>
    [HttpGet("keywords/{name}")]
    public ActionResult<KeywordDetailDto> GetKeyword(string name) => Ok(rules.GetKeyword(name));

    /// <summary>
    /// The strings the client turns into links inside card text, and the keyword each one
    /// resolves to. Which keywords are safe to match is a rules judgement, so it is made
    /// here once rather than re-derived in the client.
    /// </summary>
    [HttpGet("keyword-links")]
    public ActionResult<IReadOnlyList<KeywordLinkDto>> GetKeywordLinks() => Ok(rules.KeywordLinks);

    /// <summary>The glossary, optionally filtered, always paged.</summary>
    [HttpGet("glossary")]
    public ActionResult<GlossaryResultDto> GetGlossary([FromQuery] GlossaryRequest request) =>
        Ok(rules.GetGlossary(request));

    /// <summary>Search rules, keywords, and the glossary.</summary>
    [HttpGet("search")]
    public ActionResult<RulesSearchResultDto> Search([FromQuery] RulesSearchRequest request) =>
        Ok(rules.Search(request));
}
