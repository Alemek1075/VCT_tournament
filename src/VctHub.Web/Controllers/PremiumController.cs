using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement;
using VctHub.Web.Data;
using VctHub.Web.Services;

namespace VctHub.Web.Controllers;

public record BreakoutRow(int Id, string Nickname, string? Team, string? Photo, decimal From, decimal To, string FromEvent, string ToEvent);
public record ConsistencyRow(int Id, string Nickname, string? Team, decimal Avg, double StdDev, int Events);
public record PremiumVm(List<BreakoutRow> Breakouts, List<ConsistencyRow> Consistent, bool CanExport);

/// <summary>B6: the "special" page. Only workspaces on the Premium plan see it (feature flag PremiumStats).</summary>
[Authorize, Route("premium")]
public class PremiumController(AppDbContext db, IFeatureManager features) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Premium analytics";
        if (!await features.IsEnabledAsync(Features.PremiumStats))
            return View("Locked");

        var rows = await db.PlayerStats.AsNoTracking()
            .Where(s => s.Maps >= 4)
            .Select(s => new { s.PlayerId, s.Player!.Nickname, Team = s.Player.Team!.Tag, s.Player.PhotoUrl, s.Rating, Event = s.Tournament!.Name, s.Tournament.StartDate })
            .ToListAsync();

        var byPlayer = rows.GroupBy(r => r.PlayerId).Where(g => g.Count() >= 2).ToList();
        var breakouts = byPlayer
            .Select(g => { var o = g.OrderBy(x => x.StartDate).ToList(); var a = o.First(); var b = o.Last();
                return new BreakoutRow(g.Key, a.Nickname, a.Team, a.PhotoUrl, a.Rating, b.Rating, a.Event, b.Event); })
            .OrderByDescending(b => b.To - b.From).Take(10).ToList();
        var consistent = byPlayer.Where(g => g.Count() >= 3)
            .Select(g =>
            {
                var r = g.Select(x => (double)x.Rating).ToList();
                var avg = r.Average();
                return new ConsistencyRow(g.Key, g.First().Nickname, g.First().Team, (decimal)Math.Round(avg, 2),
                    Math.Round(Math.Sqrt(r.Sum(x => (x - avg) * (x - avg)) / r.Count), 3), r.Count);
            })
            .Where(c => c.Avg >= 1.0m).OrderBy(c => c.StdDev).Take(10).ToList();

        return View(new PremiumVm(breakouts, consistent, await features.IsEnabledAsync(Features.CsvExport)));
    }

    /// <summary>All player season stats as CSV (feature flag CsvExport).</summary>
    [HttpGet("players.csv")]
    public async Task<IActionResult> Export()
    {
        if (!await features.IsEnabledAsync(Features.CsvExport)) return NotFound();
        var players = await db.Players.AsNoTracking().Include(p => p.Team).OrderByDescending(p => p.Rating).ToListAsync();
        var sb = new StringBuilder("nickname,real_name,team,role,rating,acs,kd,adr,kast,hs,maps,kills,deaths,assists\n");
        string Q(string? s) => $"\"{(s ?? "").Replace("\"", "\"\"")}\"";
        foreach (var p in players)
            sb.AppendLine(string.Join(',', Q(p.Nickname), Q(p.RealName), Q(p.Team?.Name), Q(p.Role),
                p.Rating.ToString(CultureInfo.InvariantCulture), p.Acs, p.Kd.ToString(CultureInfo.InvariantCulture),
                p.Adr.ToString(CultureInfo.InvariantCulture), p.Kast, p.Hs, p.Maps, p.Kills, p.Deaths, p.Assists));
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "vct-2026-players.csv");
    }
}
