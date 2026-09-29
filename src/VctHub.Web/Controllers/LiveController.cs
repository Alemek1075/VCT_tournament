using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Live;
using VctHub.Web.Models;

namespace VctHub.Web.Controllers;

public class LiveController(AppDbContext db, LiveService live) : Controller
{
    /// <summary>Live now + upcoming matches that can be simulated.</summary>
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Live";
        var upcoming = await db.Matches.AsNoTracking().Include(m => m.TeamA).Include(m => m.TeamB).Include(m => m.Tournament)
            .Where(m => m.Status != MatchStatus.Completed)
            .OrderBy(m => m.ScheduledAt).Take(12).ToListAsync();
        ViewBag.Live = live.All.Where(s => s.Status == "Live").ToList();
        return View(upcoming);
    }

    /// <summary>C10: all transports side by side on one match.</summary>
    public async Task<IActionResult> Compare(int id)
    {
        var m = await db.Matches.AsNoTracking().Include(x => x.TeamA).Include(x => x.TeamB).FirstOrDefaultAsync(x => x.Id == id);
        if (m is null) return NotFound();
        ViewData["Title"] = "Transport comparison";
        return View(m);
    }
}
