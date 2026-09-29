using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Models;

namespace VctHub.Web.Api;

[Route("api/tournaments")]
public class TournamentsApiController(AppDbContext db) : ApiBase
{
    [HttpGet]
    public async Task<Page<TournamentDto>> List(string? region, string? stage, int skip = 0, int limit = 20)
    {
        var query = db.Tournaments.AsNoTracking().Include(t => t.Region).AsQueryable();
        if (!string.IsNullOrWhiteSpace(region)) query = query.Where(t => t.Region!.Code == region);
        if (!string.IsNullOrWhiteSpace(stage)) query = query.Where(t => t.Stage == stage);
        return await PageAsync(query.OrderByDescending(t => t.StartDate).ThenBy(t => t.Id), skip, limit, TournamentDto.From);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<TournamentDto>(200), ProducesResponseType(404)]
    public async Task<IActionResult> Get(int id)
    {
        var t = await db.Tournaments.AsNoTracking().Include(x => x.Region).FirstOrDefaultAsync(x => x.Id == id);
        return t is null ? NotFound404("Tournament", id) : Ok(TournamentDto.From(t));
    }

    [HttpGet("{id:int}/matches")]
    public async Task<IActionResult> Matches(int id, int skip = 0, int limit = 50)
    {
        if (!await db.Tournaments.AnyAsync(t => t.Id == id)) return NotFound404("Tournament", id);
        var query = db.Matches.AsNoTracking().Include(m => m.TeamA).Include(m => m.TeamB).Include(m => m.Tournament)
            .Where(m => m.TournamentId == id).OrderBy(m => m.ScheduledAt).ThenBy(m => m.Id);
        return Ok(await PageAsync(query, skip, limit, MatchDto.From));
    }

    [HttpPost]
    [ProducesResponseType<TournamentDto>(201), ProducesResponseType(400), ProducesResponseType(409)]
    public async Task<IActionResult> Create(TournamentInput input)
    {
        var t = new Tournament();
        if (await Apply(t, input) is { } error) return error;
        db.Tournaments.Add(t);
        await db.SaveChangesAsync();
        await db.Entry(t).Reference(x => x.Region).LoadAsync();
        return CreatedAtAction(nameof(Get), new { id = t.Id }, TournamentDto.From(t));
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType<TournamentDto>(200), ProducesResponseType(400), ProducesResponseType(404), ProducesResponseType(409)]
    public async Task<IActionResult> Update(int id, TournamentInput input)
    {
        var t = await db.Tournaments.FindAsync(id);
        if (t is null) return NotFound404("Tournament", id);
        if (await Apply(t, input) is { } error) return error;
        await db.SaveChangesAsync();
        await db.Entry(t).Reference(x => x.Region).LoadAsync();
        return Ok(TournamentDto.From(t));
    }

    /// <summary>Deletes the tournament together with its matches and stat lines.</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(204), ProducesResponseType(404)]
    public async Task<IActionResult> Delete(int id)
    {
        var t = await db.Tournaments.FindAsync(id);
        if (t is null) return NotFound404("Tournament", id);
        db.Tournaments.Remove(t);
        await db.SaveChangesAsync();
        return NoContent();
    }

    async Task<IActionResult?> Apply(Tournament t, TournamentInput input)
    {
        if (input.RegionId is { } rid && !await db.Regions.AnyAsync(r => r.Id == rid))
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
                { ["regionId"] = [$"Region {rid} does not exist"] }));
        var slug = Slug.Make(input.Name);
        if (await db.Tournaments.AnyAsync(x => x.Slug == slug && x.Id != t.Id))
            return Conflict409($"Tournament '{input.Name}' already exists");
        t.Name = input.Name;
        t.Slug = slug;
        t.Stage = input.Stage;
        t.RegionId = input.RegionId;
        t.StartDate = input.StartDate;
        t.EndDate = input.EndDate;
        t.PrizePool = input.PrizePool;
        t.Venue = input.Venue;
        t.Latitude = input.Latitude;
        t.Longitude = input.Longitude;
        t.LogoUrl = input.LogoUrl;
        return null;
    }
}
