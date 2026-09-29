using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using VctHub.Web.Services;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Models;

namespace VctHub.Web.Controllers;

/// <summary>World map of teams and event venues (C8) with mini charts (F1).</summary>
public class MapController(AppDbContext db) : Controller
{
    public IActionResult Index()
    {
        ViewData["Title"] = "Map";
        ViewData["Description"] = "Where VCT 2026 teams are based and where every event was played.";
        return View();
    }

    [HttpGet("/api/map"), OutputCache(PolicyName = ApiCache.Policy)]
    public async Task<IActionResult> Data()
    {
        var matches = await db.Matches.AsNoTracking()
            .Where(m => m.Status == MatchStatus.Completed)
            .Select(m => new { m.TeamAId, m.TeamBId, m.ScoreA, m.ScoreB, m.TournamentId })
            .ToListAsync();

        var teams = await db.Teams.AsNoTracking()
            .Where(t => t.Latitude != null && t.Longitude != null)
            .Select(t => new
            {
                t.Id, t.Name, t.Tag, t.Slug, t.City, t.Country, t.LogoUrl,
                Region = t.Region!.Code,
                Lat = t.Latitude!.Value, Lng = t.Longitude!.Value,
                Events = t.Tournaments.Select(x => x.Id).ToList(),
                Rating = t.Players.Where(p => p.Rounds > 0).Average(p => (decimal?)p.Rating) ?? 0,
            })
            .ToListAsync();

        var result = teams.Select(t =>
        {
            var mine = matches.Where(m => m.TeamAId == t.Id || m.TeamBId == t.Id).ToList();
            var wins = mine.Count(m => m.TeamAId == t.Id ? m.ScoreA > m.ScoreB : m.ScoreB > m.ScoreA);
            return new { t.Id, t.Name, t.Tag, t.Slug, t.City, t.Country, t.LogoUrl, t.Region, t.Lat, t.Lng, t.Events,
                Rating = Math.Round(t.Rating, 2), Wins = wins, Losses = mine.Count - wins };
        });

        var events = await db.Tournaments.AsNoTracking()
            .Where(t => t.Latitude != null)
            .OrderBy(t => t.StartDate)
            .Select(t => new
            {
                t.Id, t.Name, t.Stage, t.Venue, t.LogoUrl, t.StartDate, t.EndDate, t.PrizePool,
                Region = t.Region!.Code,
                Lat = t.Latitude!.Value, Lng = t.Longitude!.Value,
                Teams = t.Teams.Select(x => x.Id).ToList(),
                Matches = t.Matches.Count,
            })
            .ToListAsync();

        return Ok(new { teams = result, events });
    }
}
