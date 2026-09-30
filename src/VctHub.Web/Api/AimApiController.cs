using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Models;

namespace VctHub.Web.Api;

public record AimResult([Required, RegularExpression("^(gridshot|flick)$")] string Mode,
    [Range(0, 100_000)] int Score, [Range(0, 1000)] int Hits, [Range(0, 5000)] int Misses, [Range(50, 5000)] int AvgReactionMs);

/// <summary>F6: aim trainer leaderboard. Reading is public, saving a score needs an account.</summary>
[Route("api/aim"), OutputCache(NoStore = true)]
public class AimApiController(AppDbContext db) : ApiBase
{
    [HttpGet("leaderboard")]
    public async Task<IActionResult> Leaderboard(string mode = "gridshot")
    {
        // best round per player: take the top rows, keep each player's first (highest) one
        var top = await db.AimScores.AsNoTracking().Where(s => s.Mode == mode)
            .OrderByDescending(s => s.Score).Take(200).ToListAsync();
        return Ok(top.DistinctBy(s => s.Player).Take(10)
            .Select(s => new { s.Player, s.Score, s.Hits, s.Misses, s.AvgReactionMs, s.CreatedAt }));
    }

    [HttpPost("scores")]
    public async Task<IActionResult> Save(AimResult r)
    {
        // a 30-second round can't have more than ~6 hits per second: reject obvious fakes
        if (r.Hits > 180 || r.Score > r.Hits * 150) return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]> { ["score"] = ["Score doesn't add up"] }));
        var s = new AimScore
        {
            Mode = r.Mode, Score = r.Score, Hits = r.Hits, Misses = r.Misses, AvgReactionMs = r.AvgReactionMs,
            Player = User.FindFirstValue("display_name") ?? "player", UserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
        };
        db.AimScores.Add(s);
        await db.SaveChangesAsync();
        var rank = await db.AimScores.CountAsync(x => x.Mode == r.Mode && x.Score > r.Score) + 1;
        return StatusCode(201, new { s.Id, rank });
    }
}
