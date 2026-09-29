using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Models;
using VctHub.Web.Services;

namespace VctHub.Web.Controllers;

public class TournamentsController(AppDbContext db, IFileStorage storage) : Controller
{
    public async Task<IActionResult> Index() =>
        View(await db.Tournaments.Include(t => t.Region).AsNoTracking()
            .OrderByDescending(t => t.StartDate).ToListAsync());

    public async Task<IActionResult> Details(int id)
    {
        var t = await db.Tournaments.AsNoTracking()
            .Include(x => x.Region)
            .Include(x => x.Teams.OrderBy(tm => tm.Name))
            .Include(x => x.Matches).ThenInclude(m => m.TeamA)
            .Include(x => x.Matches).ThenInclude(m => m.TeamB)
            .FirstOrDefaultAsync(x => x.Id == id);
        return t is null ? NotFound() : View(t);
    }

    public async Task<IActionResult> Create()
    {
        await FillLists();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return View(new Tournament { StartDate = today, EndDate = today.AddDays(14) });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Name,Stage,RegionId,StartDate,EndDate,PrizePool,Venue,Latitude,Longitude,LogoUrl")] Tournament t, IFormFile? logoFile)
    {
        t.Slug = Slug.Make(t.Name);
        ModelState.Remove(nameof(Tournament.Slug));
        Validate(t);
        await SaveUpload(t, logoFile);
        if (!ModelState.IsValid)
        {
            await FillLists();
            return View(t);
        }
        db.Add(t);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"{t.Name} created";
        return RedirectToAction(nameof(Details), new { id = t.Id });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var t = await db.Tournaments.FindAsync(id);
        if (t is null) return NotFound();
        await FillLists();
        return View(t);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, IFormFile? logoFile)
    {
        var t = await db.Tournaments.FindAsync(id);
        if (t is null) return NotFound();
        await TryUpdateModelAsync(t, "", x => x.Name, x => x.Stage, x => x.RegionId, x => x.StartDate, x => x.EndDate,
            x => x.PrizePool, x => x.Venue, x => x.Latitude, x => x.Longitude, x => x.LogoUrl);
        t.Slug = Slug.Make(t.Name);
        Validate(t);
        await SaveUpload(t, logoFile);
        if (!ModelState.IsValid)
        {
            await FillLists();
            return View(t);
        }
        await db.SaveChangesAsync();
        TempData["Flash"] = "Saved";
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Delete(int id)
    {
        var t = await db.Tournaments.Include(x => x.Region).Include(x => x.Matches).FirstOrDefaultAsync(x => x.Id == id);
        return t is null ? NotFound() : View(t);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var t = await db.Tournaments.FindAsync(id);
        if (t is null) return NotFound();
        db.Tournaments.Remove(t); // matches and per-event stats cascade
        await db.SaveChangesAsync();
        TempData["Flash"] = $"{t.Name} deleted";
        return RedirectToAction(nameof(Index));
    }

    void Validate(Tournament t)
    {
        if (t.EndDate < t.StartDate)
            ModelState.AddModelError(nameof(Tournament.EndDate), "End date is before start date");
    }

    async Task SaveUpload(Tournament t, IFormFile? file)
    {
        if (file is not { Length: > 0 }) return;
        try { t.LogoUrl = await storage.SaveAsync(file, "tournaments"); }
        catch (InvalidOperationException e) { ModelState.AddModelError("logoFile", e.Message); }
    }

    async Task FillLists() =>
        ViewBag.Regions = new SelectList(await db.Regions.OrderBy(r => r.Id).ToListAsync(), nameof(Region.Id), nameof(Region.Name));
}
