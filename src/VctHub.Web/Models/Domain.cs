using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace VctHub.Web.Models;

public class Region
{
    public int Id { get; set; }

    [Required, StringLength(20)]
    public string Code { get; set; } = "";

    [Required, StringLength(60)]
    public string Name { get; set; } = "";

    [JsonIgnore] public List<Team> Teams { get; set; } = [];
    [JsonIgnore] public List<Tournament> Tournaments { get; set; } = [];
}

public class Team
{
    public int Id { get; set; }

    [Required, StringLength(80)]
    public string Name { get; set; } = "";

    [Required, StringLength(10)]
    public string Tag { get; set; } = "";

    [Required, StringLength(90)]
    public string Slug { get; set; } = "";

    [Display(Name = "Region")]
    public int RegionId { get; set; }
    public Region? Region { get; set; }

    [StringLength(60)] public string? Country { get; set; }
    [StringLength(4), Display(Name = "Country code")] public string? CountryCode { get; set; }
    [StringLength(60)] public string? City { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    [Display(Name = "Logo"), StringLength(400)]
    public string? LogoUrl { get; set; }

    [Url, StringLength(200)] public string? Website { get; set; }
    [Url, StringLength(200)] public string? Twitter { get; set; }

    [StringLength(2000)] public string? Description { get; set; }

    public int? VlrId { get; set; }

    public List<Player> Players { get; set; } = [];
    [JsonIgnore] public List<Tournament> Tournaments { get; set; } = [];
}

public class Player
{
    public int Id { get; set; }

    [Required, StringLength(40)]
    public string Nickname { get; set; } = "";

    [StringLength(80), Display(Name = "Real name")]
    public string? RealName { get; set; }

    [StringLength(4), Display(Name = "Country code")]
    public string? CountryCode { get; set; }

    [StringLength(20)] public string? Role { get; set; }
    [StringLength(20), Display(Name = "Main agent")] public string? MainAgent { get; set; }

    [Display(Name = "Photo"), StringLength(400)]
    public string? PhotoUrl { get; set; }

    [Display(Name = "Team")]
    public int? TeamId { get; set; }
    [JsonIgnore] public Team? Team { get; set; }

    [Display(Name = "Captain")] public bool IsCaptain { get; set; }

    // Season aggregates (weighted by rounds over all events)
    public int Maps { get; set; }
    public int Rounds { get; set; }
    [Column(TypeName = "numeric(4,2)")] public decimal Rating { get; set; }
    public int Acs { get; set; }
    [Column(TypeName = "numeric(4,2)"), Display(Name = "K:D")] public decimal Kd { get; set; }
    [Column(TypeName = "numeric(5,1)")] public decimal Adr { get; set; }
    public int Kast { get; set; }
    [Display(Name = "HS%")] public int Hs { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }

    [Display(Name = "Ranked account")]
    public long? RankedAccountId { get; set; }
    [JsonIgnore] public RankedAccount? RankedAccount { get; set; }

    public int? VlrId { get; set; }

    [JsonIgnore] public List<PlayerStat> EventStats { get; set; } = [];
}

/// <summary>Per-event stat line, used for the charts.</summary>
public class PlayerStat
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    [JsonIgnore] public Player? Player { get; set; }
    public int TournamentId { get; set; }
    [JsonIgnore] public Tournament? Tournament { get; set; }
    public int Maps { get; set; }
    public int Rounds { get; set; }
    [Column(TypeName = "numeric(4,2)")] public decimal Rating { get; set; }
    public int Acs { get; set; }
    [Column(TypeName = "numeric(4,2)")] public decimal Kd { get; set; }
    [Column(TypeName = "numeric(5,1)")] public decimal Adr { get; set; }
    public int Kast { get; set; }
    public int Hs { get; set; }
    [StringLength(20)] public string? TopAgent { get; set; }
}

public class Tournament
{
    public int Id { get; set; }

    [Required, StringLength(100)]
    public string Name { get; set; } = "";

    [Required, StringLength(110)]
    public string Slug { get; set; } = "";

    [Required, StringLength(30)]
    public string Stage { get; set; } = "";

    [Display(Name = "Region")]
    public int? RegionId { get; set; }
    public Region? Region { get; set; }

    [DataType(DataType.Date), Display(Name = "Start")]
    public DateOnly StartDate { get; set; }

    [DataType(DataType.Date), Display(Name = "End")]
    public DateOnly EndDate { get; set; }

    [Column(TypeName = "numeric(12,2)"), Display(Name = "Prize pool, $")]
    public decimal? PrizePool { get; set; }

    [StringLength(120)] public string? Venue { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    [Display(Name = "Logo"), StringLength(400)]
    public string? LogoUrl { get; set; }

    public int? VlrId { get; set; }

    [JsonIgnore] public List<Match> Matches { get; set; } = [];
    [JsonIgnore] public List<Team> Teams { get; set; } = [];
}

public enum MatchStatus { Upcoming, Live, Completed }

public class Match
{
    public int Id { get; set; }

    [Display(Name = "Tournament")]
    public int TournamentId { get; set; }
    public Tournament? Tournament { get; set; }

    [Display(Name = "Team A")]
    public int TeamAId { get; set; }
    public Team? TeamA { get; set; }

    [Display(Name = "Team B")]
    public int TeamBId { get; set; }
    public Team? TeamB { get; set; }

    [Display(Name = "Scheduled (UTC)")]
    public DateTime ScheduledAt { get; set; }

    public MatchStatus Status { get; set; }

    [Range(0, 3), Display(Name = "Score A")] public int ScoreA { get; set; }
    [Range(0, 3), Display(Name = "Score B")] public int ScoreB { get; set; }

    [Range(1, 5), Display(Name = "Best of")] public int BestOf { get; set; } = 3;

    [StringLength(60)] public string? Series { get; set; }

    [Display(Name = "VOD link"), Url, StringLength(300)]
    public string? VodUrl { get; set; }

    public long? VlrId { get; set; }
}

/// <summary>
/// Synthetic ranked ladder (~1M rows). Exists so the autocomplete (F2) has something big to search.
/// </summary>
public class RankedAccount
{
    public long Id { get; set; }
    [Required, StringLength(40)] public string RiotId { get; set; } = "";
    [StringLength(20)] public string Rank { get; set; } = "";
    [StringLength(20)] public string Region { get; set; } = "";
}
