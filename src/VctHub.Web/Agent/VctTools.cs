using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Docs;
using VctHub.Web.Models;
using VctHub.Web.Services;

namespace VctHub.Web.Agent;

/// <summary>
/// What an AI can do with VCT Hub. Used by the Gemini chat agent (C21) and exposed by the MCP server (C22).
/// Reads are open; create_strategy_doc writes into the caller's workspace and needs a tenant.
/// </summary>
public class VctTools(AppDbContext db, SearchService search)
{
    public async Task<object> SearchAsync(string query, string? type = null, CancellationToken ct = default)
    {
        var r = await search.SearchAsync(query, type is "team" or "player" or "event" or "match" ? type : null, 0, 8, ct);
        return new { engine = r.Engine, total = r.Total, hits = r.Hits.Select(h => new { h.Type, h.Title, h.Subtitle, h.Url }) };
    }

    public async Task<object> GetTeamAsync(string name, CancellationToken ct = default)
    {
        var t = await FindTeam(name, ct);
        if (t is null) return new { error = $"No team matches '{name}'" };
        var matches = await db.Matches.AsNoTracking().Include(m => m.TeamA).Include(m => m.TeamB).Include(m => m.Tournament)
            .Where(m => m.TeamAId == t.Id || m.TeamBId == t.Id).OrderByDescending(m => m.ScheduledAt).ToListAsync(ct);
        var done = matches.Where(m => m.Status == MatchStatus.Completed).ToList();
        bool Won(Match m) => m.TeamAId == t.Id ? m.ScoreA > m.ScoreB : m.ScoreB > m.ScoreA;
        return new
        {
            t.Name, t.Tag, region = t.Region?.Name, t.City, t.Country, url = $"/team/{t.Slug}",
            record = $"{done.Count(Won)}-{done.Count(m => !Won(m))}",
            roster = t.Players.OrderByDescending(p => p.Rating).Select(p => new { p.Nickname, p.RealName, p.Role, p.MainAgent, p.Rating, p.Acs, p.Kd, p.Adr }),
            lastResults = done.Take(5).Select(m => $"{m.TeamA!.Tag} {m.ScoreA}-{m.ScoreB} {m.TeamB!.Tag} ({m.Tournament!.Name}, {m.ScheduledAt:MMM d})"),
            upcoming = matches.Where(m => m.Status != MatchStatus.Completed).OrderBy(m => m.ScheduledAt).Take(3)
                .Select(m => $"{m.TeamA!.Tag} vs {m.TeamB!.Tag}, {m.ScheduledAt:MMM d HH:mm} UTC ({m.Status})"),
        };
    }

    public async Task<object> GetPlayerAsync(string nickname, CancellationToken ct = default)
    {
        var p = await db.Players.AsNoTracking().Include(x => x.Team)
            .Include(x => x.EventStats).ThenInclude(s => s.Tournament)
            .FirstOrDefaultAsync(x => EF.Functions.ILike(x.Nickname, nickname), ct)
            ?? await db.Players.AsNoTracking().Include(x => x.Team).Include(x => x.EventStats).ThenInclude(s => s.Tournament)
                .FirstOrDefaultAsync(x => EF.Functions.ILike(x.Nickname, $"%{nickname}%"), ct);
        if (p is null) return new { error = $"No player matches '{nickname}'" };
        return new
        {
            p.Nickname, p.RealName, team = p.Team?.Name, p.Role, p.MainAgent, p.CountryCode, url = $"/Players/Details/{p.Id}",
            season = new { p.Rating, p.Acs, p.Kd, p.Adr, kast = p.Kast + "%", hs = p.Hs + "%", p.Maps, p.Kills, p.Deaths },
            byEvent = p.EventStats.OrderBy(s => s.Tournament!.StartDate)
                .Select(s => new { evt = s.Tournament!.Name, s.Maps, s.Rating, s.Acs, s.Kd, agent = s.TopAgent }),
        };
    }

    public async Task<object> ListMatchesAsync(string status = "upcoming", string? team = null, int limit = 10, CancellationToken ct = default)
    {
        var q = db.Matches.AsNoTracking().Include(m => m.TeamA).Include(m => m.TeamB).Include(m => m.Tournament).AsQueryable();
        q = status.ToLowerInvariant() switch
        {
            "live" => q.Where(m => m.Status == MatchStatus.Live),
            "results" or "completed" => q.Where(m => m.Status == MatchStatus.Completed).OrderByDescending(m => m.ScheduledAt),
            _ => q.Where(m => m.Status == MatchStatus.Upcoming).OrderBy(m => m.ScheduledAt),
        };
        if (!string.IsNullOrWhiteSpace(team) && await FindTeam(team, ct) is { } t)
            q = q.Where(m => m.TeamAId == t.Id || m.TeamBId == t.Id);
        var list = await q.Take(Math.Clamp(limit, 1, 30)).ToListAsync(ct);
        return list.Select(m => new
        {
            id = m.Id, match = $"{m.TeamA!.Name} vs {m.TeamB!.Name}", score = m.Status == MatchStatus.Upcoming ? null : $"{m.ScoreA}-{m.ScoreB}",
            status = m.Status.ToString(), when = m.ScheduledAt.ToString("yyyy-MM-dd HH:mm") + " UTC", evt = m.Tournament!.Name, m.Series, url = $"/Matches/Details/{m.Id}",
        });
    }

    public async Task<object> TopPlayersAsync(string stat = "rating", string? role = null, int limit = 10, CancellationToken ct = default)
    {
        var q = db.Players.AsNoTracking().Include(p => p.Team).Where(p => p.Rounds >= 200);
        if (!string.IsNullOrWhiteSpace(role)) q = q.Where(p => p.Role == role);
        q = stat.ToLowerInvariant() switch
        {
            "acs" => q.OrderByDescending(p => p.Acs), "kd" => q.OrderByDescending(p => p.Kd),
            "adr" => q.OrderByDescending(p => p.Adr), "hs" => q.OrderByDescending(p => p.Hs),
            _ => q.OrderByDescending(p => p.Rating),
        };
        return (await q.Take(Math.Clamp(limit, 1, 25)).ToListAsync(ct))
            .Select((p, i) => new { rank = i + 1, p.Nickname, team = p.Team?.Tag, p.Role, p.Rating, p.Acs, p.Kd, p.Adr, p.Hs });
    }

    /// <summary>Write tool: a strategy doc in the caller's workspace, written from markdown.</summary>
    public async Task<object> CreateStrategyDocAsync(int? tenantId, string author, string title, string markdown, CancellationToken ct = default)
    {
        if (tenantId is null) return new { error = "Sign in first: documents belong to a workspace." };
        var blocks = markdown.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Select(l =>
        {
            var (type, text) = l switch
            {
                _ when l.StartsWith("### ") => ("h3", l[4..]), _ when l.StartsWith("## ") => ("h2", l[3..]),
                _ when l.StartsWith("# ") => ("h1", l[2..]), _ when l.StartsWith("- [ ] ") => ("todo", l[6..]),
                _ when l.StartsWith("- ") || l.StartsWith("* ") => ("ul", l[2..]), _ when l.StartsWith("> ") => ("quote", l[2..]),
                _ when char.IsDigit(l[0]) && l.Contains(". ") => ("ol", l[(l.IndexOf(". ") + 2)..]),
                _ => ("p", l),
            };
            return new Block { Id = Guid.NewGuid().ToString("N")[..10], Type = type, Text = text.Trim() };
        }).ToList();
        var doc = new StrategyDoc
        {
            TenantId = tenantId.Value, Title = title.Length > 120 ? title[..120] : title, UpdatedBy = author,
            BlocksJson = JsonSerializer.Serialize(blocks, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        db.StrategyDocs.Add(doc);
        await db.SaveChangesAsync(ct);
        return new { created = true, id = doc.Id, url = $"/docs/{doc.Id}", blocks = blocks.Count };
    }

    async Task<Team?> FindTeam(string name, CancellationToken ct) =>
        await db.Teams.AsNoTracking().Include(t => t.Region).Include(t => t.Players)
            .FirstOrDefaultAsync(t => EF.Functions.ILike(t.Tag, name) || EF.Functions.ILike(t.Name, name), ct)
        ?? await db.Teams.AsNoTracking().Include(t => t.Region).Include(t => t.Players)
            .FirstOrDefaultAsync(t => EF.Functions.ILike(t.Name, $"%{name}%"), ct);
}
