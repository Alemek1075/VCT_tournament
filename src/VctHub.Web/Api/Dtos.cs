using System.ComponentModel.DataAnnotations;
using VctHub.Web.Models;

namespace VctHub.Web.Api;

/// <summary>Page of results. <c>nextLink</c> is null on the last page.</summary>
public record Page<T>(IReadOnlyList<T> Value, int Count, int Skip, int Limit, string? NextLink);

public record RegionDto(int Id, string Code, string Name);

public record TeamDto(int Id, string Name, string Tag, string Slug, string Region, int RegionId, string? Country,
    string? CountryCode, string? City, double? Latitude, double? Longitude, string? LogoUrl, string? Website,
    string? Twitter, string? Description, int PlayerCount)
{
    public static TeamDto From(Team t) => new(t.Id, t.Name, t.Tag, t.Slug, t.Region?.Code ?? "", t.RegionId, t.Country,
        t.CountryCode, t.City, t.Latitude, t.Longitude, t.LogoUrl, t.Website, t.Twitter, t.Description, t.Players.Count);
}

public class TeamInput
{
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [Required, StringLength(10)] public string Tag { get; set; } = "";
    [Range(1, int.MaxValue)] public int RegionId { get; set; }
    [StringLength(60)] public string? Country { get; set; }
    [StringLength(4)] public string? CountryCode { get; set; }
    [StringLength(60)] public string? City { get; set; }
    [Range(-90, 90)] public double? Latitude { get; set; }
    [Range(-180, 180)] public double? Longitude { get; set; }
    [Url, StringLength(400)] public string? LogoUrl { get; set; }
    [Url, StringLength(200)] public string? Website { get; set; }
    [Url, StringLength(200)] public string? Twitter { get; set; }
    [StringLength(2000)] public string? Description { get; set; }
}

public record PlayerDto(int Id, string Nickname, string? RealName, string? CountryCode, string? Role, string? MainAgent,
    string? PhotoUrl, int? TeamId, string? Team, bool IsCaptain, decimal Rating, int Acs, decimal Kd, decimal Adr,
    int Kast, int Hs, int Maps, int Kills, int Deaths, int Assists)
{
    public static PlayerDto From(Player p) => new(p.Id, p.Nickname, p.RealName, p.CountryCode, p.Role, p.MainAgent,
        p.PhotoUrl, p.TeamId, p.Team?.Name, p.IsCaptain, p.Rating, p.Acs, p.Kd, p.Adr, p.Kast, p.Hs, p.Maps,
        p.Kills, p.Deaths, p.Assists);
}

public class PlayerInput
{
    [Required, StringLength(40)] public string Nickname { get; set; } = "";
    [StringLength(80)] public string? RealName { get; set; }
    [StringLength(4)] public string? CountryCode { get; set; }
    [StringLength(20)] public string? Role { get; set; }
    [StringLength(20)] public string? MainAgent { get; set; }
    [Url, StringLength(400)] public string? PhotoUrl { get; set; }
    public int? TeamId { get; set; }
    public bool IsCaptain { get; set; }
}

public record TournamentDto(int Id, string Name, string Slug, string Stage, string? Region, DateOnly StartDate,
    DateOnly EndDate, decimal? PrizePool, string? Venue, double? Latitude, double? Longitude, string? LogoUrl)
{
    public static TournamentDto From(Tournament t) => new(t.Id, t.Name, t.Slug, t.Stage, t.Region?.Code, t.StartDate,
        t.EndDate, t.PrizePool, t.Venue, t.Latitude, t.Longitude, t.LogoUrl);
}

public class TournamentInput : IValidatableObject
{
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [Required, StringLength(30)] public string Stage { get; set; } = "";
    public int? RegionId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    [Range(0, 100_000_000)] public decimal? PrizePool { get; set; }
    [StringLength(120)] public string? Venue { get; set; }
    [Range(-90, 90)] public double? Latitude { get; set; }
    [Range(-180, 180)] public double? Longitude { get; set; }
    [Url, StringLength(400)] public string? LogoUrl { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (EndDate < StartDate) yield return new("EndDate must not be before StartDate", [nameof(EndDate)]);
    }
}

public record MatchTeamDto(int Id, string Name, string Tag, string? LogoUrl);

public record MatchDto(int Id, int TournamentId, string? Tournament, MatchTeamDto? TeamA, MatchTeamDto? TeamB,
    DateTime ScheduledAt, string Status, int ScoreA, int ScoreB, int BestOf, string? Series, string? VodUrl)
{
    public static MatchDto From(Match m) => new(m.Id, m.TournamentId, m.Tournament?.Name,
        m.TeamA is { } a ? new(a.Id, a.Name, a.Tag, a.LogoUrl) : null,
        m.TeamB is { } b ? new(b.Id, b.Name, b.Tag, b.LogoUrl) : null,
        m.ScheduledAt, m.Status.ToString(), m.ScoreA, m.ScoreB, m.BestOf, m.Series, m.VodUrl);
}

public class MatchInput : IValidatableObject
{
    [Range(1, int.MaxValue)] public int TournamentId { get; set; }
    [Range(1, int.MaxValue)] public int TeamAId { get; set; }
    [Range(1, int.MaxValue)] public int TeamBId { get; set; }
    public DateTime ScheduledAt { get; set; }
    public MatchStatus Status { get; set; }
    [Range(0, 3)] public int ScoreA { get; set; }
    [Range(0, 3)] public int ScoreB { get; set; }
    [AllowedValues(1, 3, 5)] public int BestOf { get; set; } = 3;
    [StringLength(60)] public string? Series { get; set; }
    [Url, StringLength(300)] public string? VodUrl { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (TeamAId == TeamBId) yield return new("A team can't play itself", [nameof(TeamBId)]);
        var toWin = BestOf / 2 + 1;
        if (ScoreA > toWin || ScoreB > toWin) yield return new($"Best of {BestOf} ends at {toWin} maps", [nameof(ScoreA)]);
    }
}
