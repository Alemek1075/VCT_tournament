using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Models;

namespace VctHub.Web.Api;

[Route("api/matches")]
public class MatchesApiController(AppDbContext db) : ApiBase
{
    /// <summary>List matches. Filters: status (Upcoming|Live|Completed), tournamentId, teamId, from/to (UTC).</summary>
    [HttpGet]
    public async Task<Page<MatchDto>> List(MatchStatus? status, int? tournamentId, int? teamId, DateTime? from, DateTime? to,
        int skip = 0, int limit = 20)
    {
        var query = Matches();
        if (status is not null) query = query.Where(m => m.Status == status);
        if (tournamentId is not null) query = query.Where(m => m.TournamentId == tournamentId);
        if (teamId is not null) query = query.Where(m => m.TeamAId == teamId || m.TeamBId == teamId);
        if (from is not null) query = query.Where(m => m.ScheduledAt >= DateTime.SpecifyKind(from.Value, DateTimeKind.Utc));
        if (to is not null) query = query.Where(m => m.ScheduledAt < DateTime.SpecifyKind(to.Value, DateTimeKind.Utc));
        query = status == MatchStatus.Upcoming
            ? query.OrderBy(m => m.ScheduledAt).ThenBy(m => m.Id)
            : query.OrderByDescending(m => m.ScheduledAt).ThenBy(m => m.Id);
        return await PageAsync(query, skip, limit, MatchDto.From);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<MatchDto>(200), ProducesResponseType(404)]
    public async Task<IActionResult> Get(int id)
    {
        var m = await Matches().FirstOrDefaultAsync(x => x.Id == id);
        return m is null ? NotFound404("Match", id) : Ok(MatchDto.From(m));
    }

    [HttpPost]
    [ProducesResponseType<MatchDto>(201), ProducesResponseType(400)]
    public async Task<IActionResult> Create(MatchInput input)
    {
        var m = new Match();
        if (await Apply(m, input) is { } error) return error;
        db.Matches.Add(m);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = m.Id }, MatchDto.From((await Matches().FirstAsync(x => x.Id == m.Id))));
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType<MatchDto>(200), ProducesResponseType(400), ProducesResponseType(404)]
    public async Task<IActionResult> Update(int id, MatchInput input)
    {
        var m = await db.Matches.FindAsync(id);
        if (m is null) return NotFound404("Match", id);
        if (await Apply(m, input) is { } error) return error;
        await db.SaveChangesAsync();
        return Ok(MatchDto.From(await Matches().FirstAsync(x => x.Id == id)));
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(204), ProducesResponseType(404)]
    public async Task<IActionResult> Delete(int id)
    {
        var m = await db.Matches.FindAsync(id);
        if (m is null) return NotFound404("Match", id);
        db.Matches.Remove(m);
        await db.SaveChangesAsync();
        return NoContent();
    }

    IQueryable<Match> Matches() => db.Matches.AsNoTracking()
        .Include(m => m.TeamA).Include(m => m.TeamB).Include(m => m.Tournament);

    async Task<IActionResult?> Apply(Match m, MatchInput input)
    {
        var errors = new Dictionary<string, string[]>();
        if (!await db.Tournaments.AnyAsync(t => t.Id == input.TournamentId)) errors["tournamentId"] = [$"Tournament {input.TournamentId} does not exist"];
        if (!await db.Teams.AnyAsync(t => t.Id == input.TeamAId)) errors["teamAId"] = [$"Team {input.TeamAId} does not exist"];
        if (!await db.Teams.AnyAsync(t => t.Id == input.TeamBId)) errors["teamBId"] = [$"Team {input.TeamBId} does not exist"];
        if (errors.Count > 0) return ValidationProblem(new ValidationProblemDetails(errors));
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
        return null;
    }
}
