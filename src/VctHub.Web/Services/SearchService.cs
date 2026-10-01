using System.Threading.Channels;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;

namespace VctHub.Web.Services;

/// <summary>One searchable thing: a team, a player, an event or a match.</summary>
public class SearchDoc
{
    public string Id { get; set; } = "";          // "team-5"
    public string Type { get; set; } = "";        // team | player | event | match
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string? Body { get; set; }
    public string? Region { get; set; }
    public string? Image { get; set; }
    public string Url { get; set; } = "";
    public double Boost { get; set; } = 1;
}

public record SearchHit(string Type, string Title, string? Subtitle, string? Image, string Url, double Score, string? Highlight);
public record SearchResult(string Engine, long Total, int Skip, int Limit, IReadOnlyList<SearchHit> Hits, long TookMs);

/// <summary>
/// B4: full-text search. Elasticsearch when ELASTIC_URL is configured (fuzzy, boosted fields, highlights),
/// a plain Postgres ILIKE fallback otherwise so the endpoint always works.
/// </summary>
public class SearchService(IServiceScopeFactory scopes, IConfiguration config, ILogger<SearchService> log)
{
    public const string Index = "vct";
    readonly ElasticsearchClient? es = config["ELASTIC_URL"] is { Length: > 0 } url
        ? new ElasticsearchClient(new ElasticsearchClientSettings(new Uri(url)).DefaultIndex(Index))
        : null;

    public bool UsesElastic => es != null;

    public async Task<SearchResult> SearchAsync(string q, string? type, int skip, int limit, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (es != null)
        {
            try
            {
                var res = await es.SearchAsync<SearchDoc>(s => s
                    .Indices(Index)
                    .From(skip).Size(limit)
                    .Query(qd => qd.FunctionScore(fs => fs
                        .Query(inner => inner.Bool(b =>
                        {
                            b.Must(m => m.MultiMatch(mm => mm
                                .Query(q)
                                .Fields(new[] { "title^4", "subtitle^2", "body" })
                                .Fuzziness(new Fuzziness("AUTO"))
                                .Type(TextQueryType.BestFields)));
                            if (!string.IsNullOrEmpty(type))
                                b.Filter(f => f.Term(t => t.Field("type").Value(type)));
                        }))
                        .Functions(fn => fn.FieldValueFactor(fv => fv.Field("boost").Missing(1)))
                        .BoostMode(FunctionBoostMode.Multiply)))
                    .Highlight(h => h
                        .PreTags(["<mark>"]).PostTags(["</mark>"])
                        .Fields(f => f.Add("body", new HighlightField { FragmentSize = 120, NumberOfFragments = 1 })
                                      .Add("title", new HighlightField()))), ct);

                if (res.IsValidResponse)
                    return new SearchResult("elasticsearch", res.Total, skip, limit,
                        res.Hits.Select(h => new SearchHit(h.Source!.Type, h.Source.Title, h.Source.Subtitle, h.Source.Image,
                            h.Source.Url, h.Score ?? 0,
                            h.Highlight?.Values.SelectMany(v => v).FirstOrDefault())).ToList(),
                        res.Took);
                log.LogWarning("Elasticsearch search failed: {Error}", res.DebugInformation);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Elasticsearch unavailable, falling back to Postgres");
            }
        }

        // fallback: same documents, simple contains-match
        var docs = await BuildDocsAsync(ct);
        var hits = docs
            .Where(d => string.IsNullOrEmpty(type) || d.Type == type)
            .Select(d => (d, score: Score(d, q)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ToList();
        return new SearchResult("postgres", hits.Count, skip, limit,
            hits.Skip(skip).Take(limit).Select(x => new SearchHit(x.d.Type, x.d.Title, x.d.Subtitle, x.d.Image, x.d.Url, x.score, null)).ToList(),
            sw.ElapsedMilliseconds);
    }

    static double Score(SearchDoc d, string q)
    {
        double s = 0;
        if (d.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) s += 4;
        if (d.Subtitle?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) s += 2;
        if (d.Body?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) s += 1;
        return s * d.Boost;
    }

    /// <summary>Drops and rebuilds the index from the database. ~1k docs, takes well under a second.</summary>
    public async Task ReindexAsync(CancellationToken ct = default)
    {
        if (es == null) return;
        var docs = await BuildDocsAsync(ct);
        // Never drop the live index: searches made during a reindex would hit "no shards" and fall
        // back to Postgres. Create it once, then overwrite docs by id and delete the ones that are gone.
        var exists = await es.Indices.ExistsAsync(Index, ct);
        if (!exists.Exists) await CreateIndexAsync(ct);
        var bulk = await es.BulkAsync(b => b.Index(Index).IndexMany(docs, (op, d) => op.Id(d.Id)).Refresh(Refresh.True), ct);
        var keep = docs.Select(d => d.Id).ToList();
        var stale = await es.DeleteByQueryAsync<SearchDoc>(Index, d => d
            .Query(q => q.Bool(bq => bq.MustNot(mn => mn.Ids(i => i.Values(new Ids(keep))))))
            .Refresh(true), ct);
        log.LogInformation("Elasticsearch: indexed {N} docs, removed {Gone} (errors: {E})", docs.Count, stale.Deleted ?? 0, bulk.Errors);
    }

    async Task CreateIndexAsync(CancellationToken ct)
    {
        var created = await es!.Indices.CreateAsync<SearchDoc>(Index, c => c
            .Settings(s => s.Analysis(a => a
                .Analyzers(an => an.Custom("folding", cu => cu.Tokenizer("standard").Filter(["lowercase", "asciifolding"])))))
            .Mappings(m => m.Properties(p => p
                .Keyword(k => k.Type)
                .Keyword(k => k.Region)
                .Text(t => t.Title, t => t.Analyzer("folding"))
                .Text(t => t.Subtitle, t => t.Analyzer("folding"))
                .Text(t => t.Body, t => t.Analyzer("folding"))
                .DoubleNumber(n => n.Boost)
                .Keyword(k => k.Url, k => k.Index(false))
                .Keyword(k => k.Image, k => k.Index(false)))), ct);
        if (!created.IsValidResponse) log.LogWarning("Index create failed: {E}", created.DebugInformation);
    }

    async Task<List<SearchDoc>> BuildDocsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var docs = new List<SearchDoc>();

        docs.AddRange((await db.Teams.AsNoTracking().Include(t => t.Region).Include(t => t.Players).ToListAsync(ct)).Select(t => new SearchDoc
        {
            Id = $"team-{t.Id}", Type = "team", Title = t.Name, Subtitle = $"{t.Tag} · {t.Region?.Name}",
            Body = $"{t.City} {t.Country} {t.Description} roster: {string.Join(", ", t.Players.Select(p => p.Nickname))}",
            Region = t.Region?.Code, Image = t.LogoUrl, Url = $"/team/{t.Slug}", Boost = 1.5,
        }));
        docs.AddRange((await db.Players.AsNoTracking().Include(p => p.Team).ToListAsync(ct)).Select(p => new SearchDoc
        {
            Id = $"player-{p.Id}", Type = "player", Title = p.Nickname, Subtitle = $"{p.RealName} · {p.Team?.Name}",
            Body = $"{p.Role} {p.MainAgent} {p.CountryCode} rating {p.Rating} acs {p.Acs}",
            Image = p.PhotoUrl ?? p.Team?.LogoUrl, Url = $"/Players/Details/{p.Id}", Boost = 1.2,
        }));
        docs.AddRange((await db.Tournaments.AsNoTracking().Include(t => t.Region).ToListAsync(ct)).Select(t => new SearchDoc
        {
            Id = $"event-{t.Id}", Type = "event", Title = t.Name, Subtitle = $"{t.Stage} · {t.Venue}",
            Body = $"{t.StartDate:MMMM yyyy} {t.Region?.Name} prize {t.PrizePool}", Region = t.Region?.Code,
            Image = t.LogoUrl, Url = $"/Tournaments/Details/{t.Id}", Boost = 1.3,
        }));
        docs.AddRange((await db.Matches.AsNoTracking().Include(m => m.TeamA).Include(m => m.TeamB).Include(m => m.Tournament).ToListAsync(ct)).Select(m => new SearchDoc
        {
            Id = $"match-{m.Id}", Type = "match", Title = $"{m.TeamA?.Name} vs {m.TeamB?.Name}",
            Subtitle = $"{m.Tournament?.Name} · {m.Series}",
            Body = $"{m.TeamA?.Tag} {m.TeamB?.Tag} {m.ScoreA}:{m.ScoreB} {m.Status} {m.ScheduledAt:MMMM d}",
            Image = m.TeamA?.LogoUrl, Url = $"/Matches/Details/{m.Id}", Boost = 0.8,
        }));
        return docs;
    }
}

/// <summary>Reindexes on startup and (debounced) after data changes.</summary>
public class SearchIndexer(SearchService search, ILogger<SearchIndexer> log) : BackgroundService
{
    readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void RequestReindex() => signals.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!search.UsesElastic) return;
        signals.Writer.TryWrite(true);
        await foreach (var _ in signals.Reader.ReadAllAsync(ct))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct); // let a burst of edits settle
            try { await search.ReindexAsync(ct); }
            catch (Exception e) { log.LogWarning(e, "Reindex failed, will retry on next change"); }
        }
    }
}
