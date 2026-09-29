using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Models;
using Match = VctHub.Web.Models.Match;

namespace VctHub.Web.Data;

/// <summary>
/// Loads seed/vct2026.json (scraped by tools/scrape_vlr.py) into an empty database.
/// Image URLs point at Seed:ImageBaseUrl — local "/seed-img/" in dev, jsDelivr CDN in prod.
/// </summary>
public partial class Seeder(AppDbContext db, IConfiguration config, IWebHostEnvironment env, ILogger<Seeder> log)
{
    static readonly (string Code, string Name)[] RegionList =
        [("americas", "Americas"), ("emea", "EMEA"), ("pacific", "Pacific"), ("china", "China"), ("international", "International")];

    public static string SeedDir(IWebHostEnvironment env, IConfiguration config) =>
        config["Seed:Dir"] ?? Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "seed"));

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (await db.Teams.AnyAsync(ct))
        {
            await SeedRankedAsync(ct);
            return;
        }

        var dir = SeedDir(env, config);
        var file = Path.Combine(dir, "vct2026.json");
        if (!File.Exists(file))
        {
            log.LogWarning("Seed file {File} not found, skipping", file);
            return;
        }

        var imgBase = (config["Seed:ImageBaseUrl"] ?? "/seed-img/").TrimEnd('/') + "/";
        string? Img(JsonNode? n) => n?.GetValue<string>() is { } f ? imgBase + f : null;

        var data = JsonNode.Parse(await File.ReadAllTextAsync(file, ct))!;
        var geoFile = Path.Combine(dir, "geo.json");
        var geo = File.Exists(geoFile) ? JsonNode.Parse(await File.ReadAllTextAsync(geoFile, ct)) : null;

        var regions = RegionList.ToDictionary(r => r.Code, r => new Region { Code = r.Code, Name = r.Name });
        db.Regions.AddRange(regions.Values);

        // --- teams + players
        var teams = new Dictionary<int, Team>();
        var players = new Dictionary<int, Player>();
        foreach (var t in data["teams"]!.AsArray())
        {
            var vlrId = t!["vlrId"]!.GetValue<int>();
            var g = geo?["teams"]?[vlrId.ToString()];
            var team = new Team
            {
                VlrId = vlrId,
                Name = t["name"]!.GetValue<string>(),
                Tag = Str(t["tag"]) is { Length: > 0 } tag ? tag : new string(t["name"]!.GetValue<string>().Take(3).ToArray()).ToUpperInvariant(),
                Region = regions[t["region"]!.GetValue<string>()],
                Country = Str(t["country"]),
                CountryCode = Str(t["countryCode"]),
                City = Str(g?["city"]),
                Latitude = g?["lat"]?.GetValue<double>(),
                Longitude = g?["lng"]?.GetValue<double>(),
                LogoUrl = Img(t["logo"]),
                Website = Str(t["website"]),
                Twitter = Str(t["twitter"]),
            };
            team.Slug = Slug.Make(team.Name);
            teams[vlrId] = team;

            foreach (var p in t["roster"]!.AsArray())
            {
                if (Str(p!["staff"]) != null) continue;
                var pid = p["vlrId"]!.GetValue<int>();
                if (players.ContainsKey(pid)) continue;
                var player = new Player
                {
                    VlrId = pid,
                    Nickname = p["nick"]!.GetValue<string>(),
                    RealName = Str(p["real"]),
                    Role = Str(p["role"]),
                    MainAgent = Str(p["mainAgent"]),
                    PhotoUrl = Img(p["photo"]),
                    IsCaptain = p["captain"]?.GetValue<bool>() ?? false,
                    Team = team,
                };
                players[pid] = player;
            }
        }
        db.Teams.AddRange(teams.Values);

        // --- tournaments, matches, per-event stats
        var byName = teams.Values.GroupBy(t => Norm(t.Name)).ToDictionary(g => g.Key, g => g.First());
        var matchCount = 0;
        foreach (var e in data["events"]!.AsArray())
        {
            var (start, end) = ParseDates(e!["dates"]!.GetValue<string>());
            var location = Str(e["location"]);
            var g = location is null ? null : geo?["venues"]?[location];
            var tour = new Tournament
            {
                VlrId = e["vlrId"]!.GetValue<int>(),
                Name = e["name"]!.GetValue<string>(),
                Stage = e["stage"]!.GetValue<string>(),
                Region = regions[e["region"]!.GetValue<string>()],
                StartDate = start,
                EndDate = end,
                PrizePool = ParseMoney(Str(e["prize"])),
                Venue = location,
                Latitude = g?["lat"]?.GetValue<double>(),
                Longitude = g?["lng"]?.GetValue<double>(),
                LogoUrl = Img(e["logo"]),
            };
            tour.Slug = Slug.Make(tour.Name);
            foreach (var et in e["teams"]!.AsArray())
                if (teams.TryGetValue(et!["vlrId"]!.GetValue<int>(), out var team))
                    tour.Teams.Add(team);
            db.Tournaments.Add(tour);

            foreach (var m in e["matches"]!.AsArray())
            {
                var a = FindTeam(byName, m!["teamA"]!.GetValue<string>());
                var b = FindTeam(byName, m["teamB"]!.GetValue<string>());
                if (a is null || b is null) continue; // "TBD" slots in future playoff brackets
                int.TryParse(Str(m["scoreA"]), out var sa);
                int.TryParse(Str(m["scoreB"]), out var sb);
                var status = Str(m["status"]) switch
                {
                    "Completed" => MatchStatus.Completed,
                    "LIVE" => MatchStatus.Live,
                    _ => MatchStatus.Upcoming,
                };
                var series = Str(m["series"]);
                tour.Matches.Add(new Match
                {
                    VlrId = m["vlrId"]!.GetValue<long>(),
                    TeamA = a,
                    TeamB = b,
                    ScoreA = status == MatchStatus.Upcoming ? 0 : sa,
                    ScoreB = status == MatchStatus.Upcoming ? 0 : sb,
                    Status = status,
                    Series = series,
                    BestOf = series != null && series.Contains("Final", StringComparison.OrdinalIgnoreCase) && !series.Contains("Semi") ? 5 : 3,
                    ScheduledAt = ParseWhen(Str(m["day"]), Str(m["time"]), start),
                });
                matchCount++;
            }

            // player stat rows reference events by vlr id
            tourByVlr[tour.VlrId!.Value] = tour;
        }

        foreach (var t in data["teams"]!.AsArray())
        foreach (var p in t!["roster"]!.AsArray())
        {
            if (!players.TryGetValue(p!["vlrId"]!.GetValue<int>(), out var player) || p["stats"] is not JsonArray rows) continue;
            foreach (var r in rows)
            {
                if (!tourByVlr.TryGetValue(r!["event"]!.GetValue<int>(), out var tour)) continue;
                if (player.EventStats.Any(s => s.Tournament == tour)) continue;
                player.EventStats.Add(new PlayerStat
                {
                    Tournament = tour,
                    Maps = Int(r["maps"]),
                    Rounds = Int(r["rnd"]),
                    Rating = Dec(r["rating2"]),
                    Acs = Int(r["acs"]),
                    Kd = Dec(r["kd"]),
                    Adr = Dec(r["adr"]),
                    Kast = Int(r["kast"]),
                    Hs = Int(r["hsp"]),
                    TopAgent = r["agents"]?.AsArray().FirstOrDefault()?.GetValue<string>(),
                });
                player.Kills += Int(r["k"]);
                player.Deaths += Int(r["d"]);
                player.Assists += Int(r["a"]);
            }
            Aggregate(player);
        }
        db.Players.AddRange(players.Values);

        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded {Teams} teams, {Players} players, {Matches} matches", teams.Count, players.Count, matchCount);

        await SeedRankedAsync(ct);
    }

    readonly Dictionary<int, Tournament> tourByVlr = [];

    static void Aggregate(Player p)
    {
        var s = p.EventStats;
        var rounds = s.Sum(x => x.Rounds);
        if (rounds == 0) return;
        decimal W(Func<PlayerStat, decimal> f) => s.Sum(x => f(x) * x.Rounds) / rounds;
        p.Maps = s.Sum(x => x.Maps);
        p.Rounds = rounds;
        p.Rating = Math.Round(W(x => x.Rating), 2);
        p.Acs = (int)Math.Round(W(x => x.Acs));
        p.Kd = p.Deaths > 0 ? Math.Round((decimal)p.Kills / p.Deaths, 2) : Math.Round(W(x => x.Kd), 2);
        p.Adr = Math.Round(W(x => x.Adr), 1);
        p.Kast = (int)Math.Round(W(x => x.Kast));
        p.Hs = (int)Math.Round(W(x => x.Hs));
    }

    /// <summary>
    /// ~1M synthetic ranked accounts, generated inside Postgres (a few seconds instead of a million INSERTs).
    /// </summary>
    async Task SeedRankedAsync(CancellationToken ct)
    {
        var target = config.GetValue("Seed:RankedAccounts", 1_000_000);
        if (target <= 0 || await db.RankedAccounts.AnyAsync(ct)) return;
        log.LogInformation("Generating {N} ranked accounts...", target);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        await db.Database.ExecuteSqlRawAsync($"""
            SELECT setseed(0.42);
            INSERT INTO "RankedAccounts" ("RiotId", "Rank", "Region")
            SELECT
              (ARRAY['shadow','nova','viper','ghost','frost','blaze','sage','echo','spectre','raze','phantom','vandal',
                     'omen','neon','sova','jett','cypher','astra','fade','harbor','clutch','ace','flick','peek',
                     'lurk','entry','smoke','flash','dash','spike','rotate','eco','retake','ninja','tilt','oneTap'])[1 + (random()*35)::int]
              || (ARRAY['','_','x','.','0','','ua','kyiv','lviv','','god','pro','lol','tv','',''])[1 + (random()*15)::int]
              || (ARRAY['king','queen','fan','main','enjoyer','hater','lord','cat','dog','wolf','fox','bear','owl',
                        'kid','boi','girl','man','dude','bot','god','goat','rat','hero','tree','cloud'])[1 + (random()*24)::int]
              || (floor(random()*10000))::int
              || '#' || upper(substr(md5(random()::text), 1, 4)),
              (ARRAY['Iron','Bronze','Silver','Gold','Platinum','Diamond','Ascendant','Immortal','Radiant'])[1 + (power(random(), 1.6)*8)::int]
              || CASE WHEN random() < 0.97 THEN ' ' || (1 + (random()*2)::int) ELSE '' END,
              (ARRAY['EU','NA','AP','KR','BR','LATAM'])[1 + (random()*5)::int]
            FROM generate_series(1, {target});
            """, ct);
        log.LogInformation("Ranked accounts ready");
    }

    // ---------- parsing helpers

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    static int Int(JsonNode? n) => int.TryParse(Str(n)?.TrimEnd('%'), NumberStyles.Any, CultureInfo.InvariantCulture, out var i) ? i : 0;
    static decimal Dec(JsonNode? n) => decimal.TryParse(Str(n)?.TrimEnd('%'), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;

    static string Norm(string s) => NonAlnum().Replace(s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD), "");

    static Team? FindTeam(Dictionary<string, Team> byName, string name)
    {
        var n = Norm(name);
        if (byName.TryGetValue(n, out var t)) return t;
        // vlr sometimes shortens sponsor names in match lists ("Bilibili Gaming" vs "Guangzhou Huadu Bilibili Gaming")
        return byName.FirstOrDefault(kv => kv.Key.EndsWith(n) || n.EndsWith(kv.Key)).Value;
    }

    /// <summary>"Jan 15 – Feb 16, 2026", "Jun 6–21, 2026", "Sep 24 – Oct 18, 2026"</summary>
    public static (DateOnly, DateOnly) ParseDates(string s)
    {
        var m = DateRange().Match(s);
        if (!m.Success) return (DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow));
        var year = int.Parse(m.Groups["y"].Value);
        var m1 = m.Groups["m1"].Value;
        var m2 = m.Groups["m2"].Success ? m.Groups["m2"].Value : m1;
        DateOnly D(string mon, string day) =>
            DateOnly.ParseExact($"{mon} {day} {year}", "MMM d yyyy", CultureInfo.InvariantCulture);
        return (D(m1, m.Groups["d1"].Value), D(m2, m.Groups["d2"].Value));
    }

    static decimal? ParseMoney(string? s) =>
        s != null && decimal.TryParse(s.Replace("$", "").Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

    static DateTime ParseWhen(string? day, string? time, DateOnly fallback)
    {
        var dayClean = day?.Replace("Today", "").Replace("Yesterday", "").Trim().TrimEnd(',') ?? "";
        if (!DateTime.TryParseExact(dayClean, "ddd, MMMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            d = fallback.ToDateTime(TimeOnly.MinValue);
        if (DateTime.TryParseExact(time ?? "", "h:mm tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            d = d.Date + t.TimeOfDay;
        return DateTime.SpecifyKind(d, DateTimeKind.Utc);
    }

    [GeneratedRegex(@"[^a-z0-9]")] private static partial Regex NonAlnum();

    [GeneratedRegex(@"(?<m1>[A-Z][a-z]{2})\s+(?<d1>\d+)\s*[–-]\s*(?:(?<m2>[A-Z][a-z]{2})\s+)?(?<d2>\d+),\s*(?<y>\d{4})")]
    private static partial Regex DateRange();
}
