using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services.Community;

namespace MtgEngine.Api.Controllers;

[ApiController, AllowAnonymous, Route("api/explore")]
public sealed class ExploreController(ExploreService service) : ControllerBase
{
    [HttpGet("search")]
    public Task<ExploreEntry[]> Search([FromQuery] string q = "", [FromQuery] string kind = "commander") => service.Search(q, kind);

    [HttpGet("directory")]
    public Task<ExploreDirectory> SearchDirectory([FromServices] ExploreDirectoryService directory, [FromQuery] string q = "", [FromQuery] string? identity = null, [FromQuery] string mode = "within", [FromQuery] string window = "all", [FromQuery] string sort = "decks", [FromQuery] string dir = "desc", [FromQuery] int page = 1, [FromQuery] int pageSize = 40, CancellationToken ct = default) => directory.Search(new(q, identity, mode, window, sort, dir, page, pageSize), ct);

    [HttpGet("commanders")]
    public Task<ExploreDirectory> Directory(CancellationToken ct) => service.Directory(ct);

    [HttpGet("commanders/{oracleId}")]
    public Task<ExplorePage> Commander(string oracleId, [FromQuery] string variant = "", CancellationToken ct = default) =>
        service.Page(oracleId, "commander", variant, ct);

    [HttpGet("commanders/{oracleId}/average")]
    public Task<ExplorePage> Average(string oracleId, [FromQuery] string variant = "", CancellationToken ct = default) =>
        service.Page(oracleId, "average", variant, ct);

    [HttpGet("cards/{oracleId}")]
    public Task<ExplorePage> Card(string oracleId, CancellationToken ct) => service.Page(oracleId, "card", "", ct);
}
