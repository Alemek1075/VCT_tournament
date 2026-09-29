using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Models;

namespace VctHub.Web.Controllers;

public class MatchesController(AppDbContext db) : Controller
{
    public async Task<IActionResult> Index(MatchStatus? status, int? tournamentId, int page = 1)
    {
        var query = db.Matches.AsNoTracking()
            .Include(m => m.TeamA).Include(m => m.TeamB).Include(m => m.Tournament)
            .AsQueryable();
        if (status is not null) query = query.Where(m => m.Status == status);
        if (tournamentId is not null) query = query.Where(m => m.TournamentId == tournamentId);
        query = status == MatchStatus.Upcoming
            ? query.OrderBy(m => m.ScheduledAt)
            : query.OrderByDescending(m => m.ScheduledAt);
        ViewBag.Status = status;
        ViewBag.TournamentId = tournamentId;
        ViewBag.Tournaments = new SelectList(await db.Tournaments.OrderByDescending(t => t.StartDate).ToListAsync(), "Id", "Name", tournamentId);
        return View(await PagedList<Match>.CreateAsync(query, page, 30));
    }

    public async Task<IActionResult> Details(int id)
    {
        var m = await Load(id);
        return m is null ? NotFound() : View(m);
    }

    public async Task<IActionResult> Create(int? tournamentId)
    {
        var m = new Match
        {
            TournamentId = tournamentId ?? 0,
            ScheduledAt = DateTime.UtcNow.Date.AddDays(1).AddHours(17),
        };
        await Hydrate(m);
        return View(m);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("TournamentId,TeamAId,TeamBId,ScheduledAt,Status,ScoreA,ScoreB,BestOf,Series,VodUrl")] Match m)
    {
        Validate(m);
        if (!ModelState.IsValid)
        {
            await Hydrate(m);
            return View(m);
        }
        m.ScheduledAt = DateTime.SpecifyKind(m.ScheduledAt, DateTimeKind.Utc);
        db.Add(m);
        await db.SaveChangesAsync();
        TempData["Flash"] = "Match created";
        return RedirectToAction(nameof(Details), new { id = m.Id });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var m = await Load(id);
        if (m is null) return NotFound();
        return View(m);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,TournamentId,TeamAId,TeamBId,ScheduledAt,Status,ScoreA,ScoreB,BestOf,Series,VodUrl")] Match input)
    {
        var m = await db.Matches.FindAsync(id);
        if (m is null) return NotFound();
        Validate(input);
        if (!ModelState.IsValid)
        {
            await Hydrate(input);
            return View(input);
        }
        m.TournamentId = input.TournamentId;
        m.TeamAId = input.TeamAId;
        m.TeamBId = input.TeamBId;
        m.ScheduledAt = DateTime.SpecifyKind(input.ScheduledAt, DateTimeKind.Utc);
        m.Status = input.Status;
        m.ScoreA = input.ScoreA;
        m.ScoreB = input.ScoreB;
        m.BestOf = input.BestOf;
        m.Series = input.Series;
        m.VodUrl = input.VodUrl;
        await db.SaveChangesAsync();
        TempData["Flash"] = "Saved";
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Delete(int id)
    {
        var m = await Load(id);
        return m is null ? NotFound() : View(m);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var m = await db.Matches.FindAsync(id);
        if (m is null) return NotFound();
        db.Matches.Remove(m);
        await db.SaveChangesAsync();
        TempData["Flash"] = "Match deleted";
        return RedirectToAction(nameof(Index));
    }

    Task<Match?> Load(int id) => db.Matches.AsNoTracking()
        .Include(m => m.TeamA).Include(m => m.TeamB).Include(m => m.Tournament)
        .FirstOrDefaultAsync(m => m.Id == id);

    void Validate(Match m)
    {
        if (m.TeamAId == m.TeamBId)
            ModelState.AddModelError(nameof(Match.TeamBId), "A team can't play itself");
        var toWin = m.BestOf / 2 + 1;
        if (m.ScoreA > toWin || m.ScoreB > toWin)
            ModelState.AddModelError(nameof(Match.ScoreA), $"Best of {m.BestOf} ends at {toWin} maps");
    }

    // selects are autocomplete boxes; render just the currently chosen items
    async Task Hydrate(Match m)
    {
        m.Tournament ??= await db.Tournaments.FindAsync(m.TournamentId);
        m.TeamA ??= await db.Teams.FindAsync(m.TeamAId);
        m.TeamB ??= await db.Teams.FindAsync(m.TeamBId);
    }
}
