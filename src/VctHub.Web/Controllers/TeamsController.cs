using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Models;
using VctHub.Web.Services;

namespace VctHub.Web.Controllers;

public class TeamsController(AppDbContext db, IFileStorage storage) : Controller
{
    public async Task<IActionResult> Index(string? q, string? region, int page = 1)
    {
        var query = db.Teams.Include(t => t.Region).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(t => EF.Functions.ILike(t.Name, $"%{q}%") || EF.Functions.ILike(t.Tag, $"%{q}%"));
        if (!string.IsNullOrWhiteSpace(region))
            query = query.Where(t => t.Region!.Code == region);
        ViewBag.Region = region;
        ViewBag.Regions = await db.Regions.Where(r => r.Code != "international").ToListAsync();
        return View(await PagedList<Team>.CreateAsync(query.OrderBy(t => t.Name), page, 24, q));
    }

    // Landing page for a team, reachable as /team/{slug} (C6) or /Teams/Details/5
    [HttpGet("/team/{slug}")]
    public async Task<IActionResult> Landing(string slug)
    {
        var team = await db.Teams.AsNoTracking()
            .Include(t => t.Region)
            .Include(t => t.Players.OrderByDescending(p => p.Rating))
            .FirstOrDefaultAsync(t => t.Slug == slug);
        if (team is null) return NotFound();

        var matches = await db.Matches.AsNoTracking()
            .Include(m => m.TeamA).Include(m => m.TeamB).Include(m => m.Tournament)
            .Where(m => m.TeamAId == team.Id || m.TeamBId == team.Id)
            .OrderByDescending(m => m.ScheduledAt)
            .ToListAsync();
        return View("Details", new TeamPageVm(team, matches));
    }

    public async Task<IActionResult> Details(int id)
    {
        var slug = await db.Teams.Where(t => t.Id == id).Select(t => t.Slug).FirstOrDefaultAsync();
        return slug is null ? NotFound() : RedirectToActionPermanent(nameof(Landing), new { slug });
    }

    public async Task<IActionResult> Create()
    {
        await FillLists();
        return View(new Team());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Name,Tag,RegionId,Country,CountryCode,City,Latitude,Longitude,Website,Twitter,Description,LogoUrl")] Team team, IFormFile? logoFile)
    {
        team.Slug = Slug.Make(team.Name);
        ModelState.Remove(nameof(Team.Slug));
        if (await db.Teams.AnyAsync(t => t.Slug == team.Slug))
            ModelState.AddModelError(nameof(Team.Name), "A team with this name already exists");
        await SaveUpload(team, logoFile);
        if (!ModelState.IsValid)
        {
            await FillLists();
            return View(team);
        }
        db.Add(team);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Team {team.Name} created";
        return RedirectToAction(nameof(Landing), new { slug = team.Slug });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var team = await db.Teams.FindAsync(id);
        if (team is null) return NotFound();
        await FillLists();
        return View(team);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, IFormFile? logoFile)
    {
        var team = await db.Teams.FindAsync(id);
        if (team is null) return NotFound();
        if (!await TryUpdateModelAsync(team, "",
                t => t.Name, t => t.Tag, t => t.RegionId, t => t.Country, t => t.CountryCode, t => t.City,
                t => t.Latitude, t => t.Longitude, t => t.Website, t => t.Twitter, t => t.Description, t => t.LogoUrl))
        {
            await FillLists();
            return View(team);
        }
        team.Slug = Slug.Make(team.Name);
        await SaveUpload(team, logoFile);
        if (!ModelState.IsValid)
        {
            await FillLists();
            return View(team);
        }
        await db.SaveChangesAsync();
        TempData["Flash"] = "Saved";
        return RedirectToAction(nameof(Landing), new { slug = team.Slug });
    }

    public async Task<IActionResult> Delete(int id)
    {
        var team = await db.Teams.Include(t => t.Region).FirstOrDefaultAsync(t => t.Id == id);
        return team is null ? NotFound() : View(team);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var team = await db.Teams.FindAsync(id);
        if (team is null) return NotFound();
        if (await db.Matches.AnyAsync(m => m.TeamAId == id || m.TeamBId == id))
        {
            TempData["Flash"] = $"{team.Name} has matches — delete them first";
            return RedirectToAction(nameof(Delete), new { id });
        }
        db.Teams.Remove(team);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Team {team.Name} deleted";
        return RedirectToAction(nameof(Index));
    }

    async Task SaveUpload(Team team, IFormFile? file)
    {
        if (file is not { Length: > 0 }) return;
        try { team.LogoUrl = await storage.SaveAsync(file, "teams"); }
        catch (InvalidOperationException e) { ModelState.AddModelError("logoFile", e.Message); }
    }

    async Task FillLists() =>
        ViewBag.Regions = new SelectList(await db.Regions.OrderBy(r => r.Id).ToListAsync(), nameof(Region.Id), nameof(Region.Name));
}

public record TeamPageVm(Team Team, List<Match> Matches);
