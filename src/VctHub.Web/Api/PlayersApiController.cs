using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Models;

namespace VctHub.Web.Api;

[Route("api/players")]
public class PlayersApiController(AppDbContext db) : ApiBase
{
    /// <summary>List players. Filters: q, role, teamId. sort: rating (default) | acs | kd | adr | name.</summary>
    [HttpGet]
    public async Task<Page<PlayerDto>> List(string? q, string? role, int? teamId, string sort = "rating", int skip = 0, int limit = 20)
    {
        var query = db.Players.AsNoTracking().Include(p => p.Team).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(p => EF.Functions.ILike(p.Nickname, $"%{q}%") || EF.Functions.ILike(p.RealName!, $"%{q}%"));
        if (!string.IsNullOrWhiteSpace(role)) query = query.Where(p => p.Role == role);
        if (teamId is not null) query = query.Where(p => p.TeamId == teamId);
        query = sort switch
        {
            "acs" => query.OrderByDescending(p => p.Acs).ThenBy(p => p.Id),
            "kd" => query.OrderByDescending(p => p.Kd).ThenBy(p => p.Id),
            "adr" => query.OrderByDescending(p => p.Adr).ThenBy(p => p.Id),
            "name" => query.OrderBy(p => p.Nickname).ThenBy(p => p.Id),
            _ => query.OrderByDescending(p => p.Rating).ThenBy(p => p.Id),
        };
        return await PageAsync(query, skip, limit, PlayerDto.From);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<PlayerDto>(200), ProducesResponseType(404)]
    public async Task<IActionResult> Get(int id)
    {
        var p = await db.Players.AsNoTracking().Include(x => x.Team).FirstOrDefaultAsync(x => x.Id == id);
        return p is null ? NotFound404("Player", id) : Ok(PlayerDto.From(p));
    }

    /// <summary>Per-event stat lines (used by the charts).</summary>
    [HttpGet("{id:int}/stats")]
    public async Task<IActionResult> Stats(int id)
    {
        if (!await db.Players.AnyAsync(p => p.Id == id)) return NotFound404("Player", id);
        var rows = await db.PlayerStats.AsNoTracking().Where(s => s.PlayerId == id)
            .OrderBy(s => s.Tournament!.StartDate)
            .Select(s => new { s.TournamentId, Tournament = s.Tournament!.Name, s.Maps, s.Rating, s.Acs, s.Kd, s.Adr, s.Kast, s.Hs, s.TopAgent })
            .ToListAsync();
        return Ok(rows);
    }

    [HttpPost]
    [ProducesResponseType<PlayerDto>(201), ProducesResponseType(400)]
    public async Task<IActionResult> Create(PlayerInput input)
    {
        var p = new Player();
        if (await Apply(p, input) is { } error) return error;
        db.Players.Add(p);
        await db.SaveChangesAsync();
        await db.Entry(p).Reference(x => x.Team).LoadAsync();
        return CreatedAtAction(nameof(Get), new { id = p.Id }, PlayerDto.From(p));
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType<PlayerDto>(200), ProducesResponseType(400), ProducesResponseType(404)]
    public async Task<IActionResult> Update(int id, PlayerInput input)
    {
        var p = await db.Players.FindAsync(id);
        if (p is null) return NotFound404("Player", id);
        if (await Apply(p, input) is { } error) return error;
        await db.SaveChangesAsync();
        await db.Entry(p).Reference(x => x.Team).LoadAsync();
        return Ok(PlayerDto.From(p));
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(204), ProducesResponseType(404)]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await db.Players.FindAsync(id);
        if (p is null) return NotFound404("Player", id);
        db.Players.Remove(p);
        await db.SaveChangesAsync();
        return NoContent();
    }

    async Task<IActionResult?> Apply(Player p, PlayerInput input)
    {
        if (input.TeamId is { } tid && !await db.Teams.AnyAsync(t => t.Id == tid))
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
                { ["teamId"] = [$"Team {tid} does not exist"] }));
        p.Nickname = input.Nickname;
        p.RealName = input.RealName;
        p.CountryCode = input.CountryCode;
        p.Role = input.Role;
        p.MainAgent = input.MainAgent;
        p.PhotoUrl = input.PhotoUrl;
        p.TeamId = input.TeamId;
        p.IsCaptain = input.IsCaptain;
        return null;
    }
}
