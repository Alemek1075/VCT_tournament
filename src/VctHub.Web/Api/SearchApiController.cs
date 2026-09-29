using Microsoft.AspNetCore.Mvc;
using VctHub.Web.Services;

namespace VctHub.Web.Api;

[Route("api/search")]
public class SearchApiController(SearchService search) : ApiBase
{
    /// <summary>Full-text search over teams, players, events and matches. type: team | player | event | match.</summary>
    [HttpGet]
    [ProducesResponseType<SearchResult>(200), ProducesResponseType(400)]
    public async Task<IActionResult> Search(string? q, string? type, int skip = 0, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]> { ["q"] = ["At least 2 characters"] }));
        if (type is not (null or "team" or "player" or "event" or "match"))
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]> { ["type"] = ["team, player, event or match"] }));
        var res = await search.SearchAsync(q.Trim(), type, Math.Max(0, skip), Math.Clamp(limit, 1, MaxLimit), ct);
        Response.Headers["X-Search-Engine"] = res.Engine;
        return Ok(res);
    }
}
