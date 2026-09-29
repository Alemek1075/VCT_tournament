using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace VctHub.Web.Api;

[ApiController]
[Produces("application/json")]
public abstract class ApiBase : ControllerBase
{
    public const int MaxLimit = 100;

    /// <summary>
    /// skip/limit paging. nextLink repeats the current query string with skip moved forward,
    /// so filters survive page changes.
    /// </summary>
    protected async Task<Page<TOut>> PageAsync<TIn, TOut>(IQueryable<TIn> query, int skip, int limit, Func<TIn, TOut> map)
    {
        skip = Math.Max(0, skip);
        limit = Math.Clamp(limit, 1, MaxLimit);
        var count = await query.CountAsync();
        var items = await query.Skip(skip).Take(limit).ToListAsync();

        string? next = null;
        if (skip + limit < count)
        {
            var q = Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            q["skip"] = (skip + limit).ToString();
            q["limit"] = limit.ToString();
            next = $"{Request.Scheme}://{Request.Host}{Request.PathBase}{Request.Path}{QueryString.Create(q!)}";
        }
        return new Page<TOut>(items.Select(map).ToList(), count, skip, limit, next);
    }

    protected ObjectResult Conflict409(string detail) =>
        Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: detail);

    protected ObjectResult NotFound404(string what, object id) =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"{what} {id} does not exist");
}
