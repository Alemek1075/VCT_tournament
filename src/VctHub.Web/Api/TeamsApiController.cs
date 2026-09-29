using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Models;

namespace VctHub.Web.Api;

[Route("api/teams")]
public class TeamsApiController(AppDbContext db) : ApiBase
{
    /// <summary>List teams. Filters: q (name/tag), region (americas|emea|pacific|china).</summary>
    [HttpGet]
    public async Task<Page<TeamDto>> List(string? q, string? region, int skip = 0, int limit = 20)
    {
        var query = db.Teams.AsNoTracking().Include(t => t.Region).Include(t => t.Players).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(t => EF.Functions.ILike(t.Name, $"%{q}%") || EF.Functions.ILike(t.Tag, $"%{q}%"));
        if (!string.IsNullOrWhiteSpace(region))
            query = query.Where(t => t.Region!.Code == region);
        return await PageAsync(query.OrderBy(t => t.Name).AsSplitQuery(), skip, limit, TeamDto.From);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<TeamDto>(200), ProducesResponseType(404)]
    public async Task<IActionResult> Get(int id)
    {
        var t = await db.Teams.AsNoTracking().Include(x => x.Region).Include(x => x.Players).FirstOrDefaultAsync(x => x.Id == id);
        return t is null ? NotFound404("Team", id) : Ok(TeamDto.From(t));
    }

    [HttpGet("{id:int}/players")]
    public async Task<IActionResult> Players(int id)
    {
        if (!await db.Teams.AnyAsync(t => t.Id == id)) return NotFound404("Team", id);
        var players = await db.Players.AsNoTracking().Include(p => p.Team).Where(p => p.TeamId == id)
            .OrderByDescending(p => p.Rating).ToListAsync();
        return Ok(players.Select(PlayerDto.From));
    }

    [HttpPost]
    [ProducesResponseType<TeamDto>(201), ProducesResponseType(400), ProducesResponseType(409)]
    public async Task<IActionResult> Create(TeamInput input)
    {
        var team = new Team();
        if (await Apply(team, input) is { } error) return error;
        db.Teams.Add(team);
        await db.SaveChangesAsync();
        await db.Entry(team).Reference(t => t.Region).LoadAsync();
        return CreatedAtAction(nameof(Get), new { id = team.Id }, TeamDto.From(team));
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType<TeamDto>(200), ProducesResponseType(400), ProducesResponseType(404), ProducesResponseType(409)]
    public async Task<IActionResult> Update(int id, TeamInput input)
    {
        var team = await db.Teams.Include(t => t.Players).FirstOrDefaultAsync(t => t.Id == id);
        if (team is null) return NotFound404("Team", id);
        if (await Apply(team, input) is { } error) return error;
        await db.SaveChangesAsync();
        await db.Entry(team).Reference(t => t.Region).LoadAsync();
        return Ok(TeamDto.From(team));
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(204), ProducesResponseType(404), ProducesResponseType(409)]
    public async Task<IActionResult> Delete(int id)
    {
        var team = await db.Teams.FindAsync(id);
        if (team is null) return NotFound404("Team", id);
        if (await db.Matches.AnyAsync(m => m.TeamAId == id || m.TeamBId == id))
            return Conflict409($"{team.Name} still has matches; delete them first");
        db.Teams.Remove(team);
        await db.SaveChangesAsync();
        return NoContent();
    }

    async Task<IActionResult?> Apply(Team team, TeamInput input)
    {
        if (!await db.Regions.AnyAsync(r => r.Id == input.RegionId))
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
                { ["regionId"] = [$"Region {input.RegionId} does not exist"] }));
        var slug = Slug.Make(input.Name);
        if (await db.Teams.AnyAsync(t => t.Slug == slug && t.Id != team.Id))
            return Conflict409($"Team '{input.Name}' already exists");
        team.Name = input.Name;
        team.Slug = slug;
        team.Tag = input.Tag;
        team.RegionId = input.RegionId;
        team.Country = input.Country;
        team.CountryCode = input.CountryCode;
        team.City = input.City;
        team.Latitude = input.Latitude;
        team.Longitude = input.Longitude;
        team.LogoUrl = input.LogoUrl;
        team.Website = input.Website;
        team.Twitter = input.Twitter;
        team.Description = input.Description;
        return null;
    }
}

[Route("api/regions")]
public class RegionsApiController(AppDbContext db) : ApiBase
{
    [HttpGet]
    public async Task<IEnumerable<RegionDto>> List() =>
        await db.Regions.AsNoTracking().OrderBy(r => r.Id).Select(r => new RegionDto(r.Id, r.Code, r.Name)).ToListAsync();
}
