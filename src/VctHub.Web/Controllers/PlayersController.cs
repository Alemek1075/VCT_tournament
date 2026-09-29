using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Models;
using VctHub.Web.Services;

namespace VctHub.Web.Controllers;

public class PlayersController(AppDbContext db, IFileStorage storage) : Controller
{
    public static readonly string[] Roles = ["Duelist", "Initiator", "Controller", "Sentinel", "Flex"];

    public async Task<IActionResult> Index(string? q, string? role, string sort = "rating", int page = 1)
    {
        var query = db.Players.Include(p => p.Team).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(p => EF.Functions.ILike(p.Nickname, $"%{q}%") || EF.Functions.ILike(p.RealName!, $"%{q}%"));
        if (!string.IsNullOrWhiteSpace(role))
            query = query.Where(p => p.Role == role);
        query = sort switch
        {
            "acs" => query.OrderByDescending(p => p.Acs),
            "kd" => query.OrderByDescending(p => p.Kd),
            "adr" => query.OrderByDescending(p => p.Adr),
            "name" => query.OrderBy(p => p.Nickname),
            _ => query.OrderByDescending(p => p.Rating),
        };
        ViewBag.Role = role;
        ViewBag.Sort = sort;
        return View(await PagedList<Player>.CreateAsync(query, page, 30, q));
    }

    public async Task<IActionResult> Details(int id)
    {
        var player = await db.Players.AsNoTracking()
            .Include(p => p.Team)
            .Include(p => p.RankedAccount)
            .Include(p => p.EventStats).ThenInclude(s => s.Tournament)
            .FirstOrDefaultAsync(p => p.Id == id);
        return player is null ? NotFound() : View(player);
    }

    public async Task<IActionResult> Create(int? teamId)
    {
        await FillLists();
        return View(new Player { TeamId = teamId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Nickname,RealName,CountryCode,Role,MainAgent,TeamId,IsCaptain,PhotoUrl,RankedAccountId")] Player player, IFormFile? photoFile)
    {
        await SaveUpload(player, photoFile);
        if (!ModelState.IsValid)
        {
            await FillLists();
            return View(player);
        }
        db.Add(player);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Player {player.Nickname} created";
        return RedirectToAction(nameof(Details), new { id = player.Id });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var player = await db.Players.Include(p => p.Team).Include(p => p.RankedAccount).FirstOrDefaultAsync(p => p.Id == id);
        if (player is null) return NotFound();
        await FillLists();
        return View(player);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, IFormFile? photoFile)
    {
        var player = await db.Players.FindAsync(id);
        if (player is null) return NotFound();
        await TryUpdateModelAsync(player, "",
            p => p.Nickname, p => p.RealName, p => p.CountryCode, p => p.Role, p => p.MainAgent,
            p => p.TeamId, p => p.IsCaptain, p => p.PhotoUrl, p => p.RankedAccountId);
        await SaveUpload(player, photoFile);
        if (!ModelState.IsValid)
        {
            await FillLists();
            return View(player);
        }
        await db.SaveChangesAsync();
        TempData["Flash"] = "Saved";
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Delete(int id)
    {
        var player = await db.Players.Include(p => p.Team).FirstOrDefaultAsync(p => p.Id == id);
        return player is null ? NotFound() : View(player);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var player = await db.Players.FindAsync(id);
        if (player is null) return NotFound();
        db.Players.Remove(player);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Player {player.Nickname} deleted";
        return RedirectToAction(nameof(Index));
    }

    async Task SaveUpload(Player player, IFormFile? file)
    {
        if (file is not { Length: > 0 }) return;
        try { player.PhotoUrl = await storage.SaveAsync(file, "players"); }
        catch (InvalidOperationException e) { ModelState.AddModelError("photoFile", e.Message); }
    }

    async Task FillLists()
    {
        ViewBag.Teams = new SelectList(await db.Teams.OrderBy(t => t.Name).ToListAsync(), nameof(Team.Id), nameof(Team.Name));
        ViewBag.Roles = new SelectList(Roles);
    }
}
