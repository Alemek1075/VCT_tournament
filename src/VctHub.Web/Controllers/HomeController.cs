using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Models;

namespace VctHub.Web.Controllers;

public class HomeController(AppDbContext db) : Controller
{
    public async Task<IActionResult> Index()
    {
        var matches = db.Matches.AsNoTracking().Include(m => m.TeamA).Include(m => m.TeamB).Include(m => m.Tournament);
        var vm = new HomeVm(
            Live: await matches.Where(m => m.Status == MatchStatus.Live).ToListAsync(),
            Upcoming: await matches.Where(m => m.Status == MatchStatus.Upcoming).OrderBy(m => m.ScheduledAt).Take(8).ToListAsync(),
            Results: await matches.Where(m => m.Status == MatchStatus.Completed).OrderByDescending(m => m.ScheduledAt).Take(8).ToListAsync(),
            Current: await db.Tournaments.AsNoTracking().OrderByDescending(t => t.StartDate).FirstOrDefaultAsync(),
            TopPlayers: await db.Players.AsNoTracking().Include(p => p.Team).Where(p => p.Rounds > 200)
                .OrderByDescending(p => p.Rating).Take(5).ToListAsync());
        return View(vm);
    }

    [HttpGet("/search")]
    public IActionResult Search() => View();

    [Route("/Home/Status/{code:int}")]
    public IActionResult Status(int code)
    {
        Response.StatusCode = code;
        return View(code);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() =>
        View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}

public record HomeVm(List<Match> Live, List<Match> Upcoming, List<Match> Results, Tournament? Current, List<Player> TopPlayers);
