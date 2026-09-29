using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;

namespace VctHub.Web.Api;

/// <summary>
/// Autocomplete sources for the edit forms (F2). Response shape is what Select2 expects:
/// { results: [{ id, text, ... }], pagination: { more } }.
/// </summary>
[Route("api/lookup")]
public class LookupApiController(AppDbContext db) : ApiBase
{
    public const int MinChars = 3;
    const int PageSize = 20;

    public record Item(object Id, string Text, string? Image = null, string? Sub = null);
    public record Result(IReadOnlyList<Item> Results, object Pagination);

    /// <summary>~1M synthetic ranked accounts; "contains" search backed by a pg_trgm GIN index.</summary>
    [HttpGet("ranked")]
    public async Task<ActionResult<Result>> Ranked(string? term, int page = 1)
    {
        if (term is null || term.Trim().Length < MinChars) return Empty();
        var pattern = $"%{Escape(term.Trim())}%";
        var rows = await db.RankedAccounts.AsNoTracking()
            .Where(a => EF.Functions.ILike(a.RiotId, pattern))
            .OrderBy(a => a.RiotId.Length).ThenBy(a => a.Id)
            .Skip((Math.Max(page, 1) - 1) * PageSize).Take(PageSize + 1)
            .Select(a => new Item(a.Id, a.RiotId, null, $"{a.Rank} · {a.Region}"))
            .ToListAsync();
        return new Result(rows.Take(PageSize).ToList(), new { more = rows.Count > PageSize });
    }

    [HttpGet("teams")]
    public async Task<ActionResult<Result>> Teams(string? term, int page = 1)
    {
        if (term is null || term.Trim().Length < MinChars) return Empty();
        var pattern = $"%{Escape(term.Trim())}%";
        var rows = await db.Teams.AsNoTracking()
            .Where(t => EF.Functions.ILike(t.Name, pattern) || EF.Functions.ILike(t.Tag, pattern) || EF.Functions.ILike(t.City!, pattern))
            .OrderBy(t => t.Name)
            .Skip((Math.Max(page, 1) - 1) * PageSize).Take(PageSize + 1)
            .Select(t => new Item(t.Id, t.Name, t.LogoUrl, t.Tag + " · " + t.Region!.Name))
            .ToListAsync();
        return new Result(rows.Take(PageSize).ToList(), new { more = rows.Count > PageSize });
    }

    [HttpGet("tournaments")]
    public async Task<ActionResult<Result>> Tournaments(string? term, int page = 1)
    {
        if (term is null || term.Trim().Length < MinChars) return Empty();
        var pattern = $"%{Escape(term.Trim())}%";
        var rows = await db.Tournaments.AsNoTracking()
            .Where(t => EF.Functions.ILike(t.Name, pattern) || EF.Functions.ILike(t.Venue!, pattern))
            .OrderByDescending(t => t.StartDate)
            .Skip((Math.Max(page, 1) - 1) * PageSize).Take(PageSize + 1)
            .Select(t => new Item(t.Id, t.Name, t.LogoUrl, t.Venue))
            .ToListAsync();
        return new Result(rows.Take(PageSize).ToList(), new { more = rows.Count > PageSize });
    }

    static Result Empty() => new([], new { more = false });

    // user input must not turn into LIKE wildcards
    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
