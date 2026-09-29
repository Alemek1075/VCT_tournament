using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Models;

namespace VctHub.Web.Live;

/// <summary>Snapshot of a live match. Version grows by one with every round.</summary>
public record LiveState(
    int MatchId, long Version, string TeamA, string TeamB, string? LogoA, string? LogoB,
    int BestOf, int MapsA, int MapsB, int MapNumber, string MapName, int RoundsA, int RoundsB,
    string LastEvent, string Status, long ServerTime);

/// <summary>
/// Plays out matches round by round and fans each change out to every transport:
/// long-poll waiters, raw WebSockets (via Changed event) and SignalR groups.
/// </summary>
public class LiveService(IServiceScopeFactory scopes, IHubContext<LiveHub> hub, IConfiguration config, ILogger<LiveService> log)
{
    static readonly string[] Maps = ["Ascent", "Bind", "Haven", "Lotus", "Sunset", "Pearl", "Split", "Abyss", "Corrode"];
    static readonly string[] Plays = [
        "{0} win the retake", "{0} take the round with a 4k", "{0} clutch a 1v2", "{0} win the eco round",
        "{0} plant and hold", "{0} defuse with 0.4s left", "{0} win the pistol round", "{0} flawless the round",
        "{0} force-buy and steal it", "{0} win the post-plant",
    ];

    readonly ConcurrentDictionary<int, LiveState> states = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<LiveState>> waiters = new();
    readonly ConcurrentDictionary<int, CancellationTokenSource> runs = new();

    /// <summary>Raised after every change (raw WebSocket connections subscribe to this).</summary>
    public event Action<LiveState>? Changed;

    public TimeSpan RoundDelay => TimeSpan.FromMilliseconds(config.GetValue("Live:RoundMs", 3000));

    public LiveState? Get(int matchId) => states.GetValueOrDefault(matchId);
    public IEnumerable<LiveState> All => states.Values;
    public bool IsRunning(int matchId) => runs.ContainsKey(matchId);

    /// <summary>Long poll: completes when a version newer than <paramref name="since"/> exists, or on timeout.</summary>
    public async Task<LiveState?> WaitAsync(int matchId, long since, TimeSpan timeout, CancellationToken ct)
    {
        if (states.TryGetValue(matchId, out var s) && s.Version > since) return s;
        var tcs = waiters.GetOrAdd(matchId, _ => new TaskCompletionSource<LiveState>(TaskCreationOptions.RunContinuationsAsynchronously));
        // the state may have moved between the check and GetOrAdd
        if (states.TryGetValue(matchId, out s) && s.Version > since) return s;
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    public async Task<LiveState> StartAsync(int matchId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var m = await db.Matches.Include(x => x.TeamA).Include(x => x.TeamB).FirstOrDefaultAsync(x => x.Id == matchId)
                ?? throw new KeyNotFoundException($"Match {matchId} not found");

        Stop(matchId);
        m.Status = MatchStatus.Live;
        m.ScoreA = m.ScoreB = 0;
        await db.SaveChangesAsync();

        var rng = new Random(matchId);
        var state = new LiveState(m.Id, 1, m.TeamA!.Name, m.TeamB!.Name, m.TeamA.LogoUrl, m.TeamB.LogoUrl, m.BestOf,
            0, 0, 1, Maps[rng.Next(Maps.Length)], 0, 0, "Match is live", "Live", Now());
        Publish(state);

        if (MatchStarted is { } started)
            try { await started(state); }
            catch (Exception e) { log.LogWarning(e, "MatchStarted handler failed for {Id}", matchId); }

        var cts = new CancellationTokenSource();
        runs[matchId] = cts;
        _ = Task.Run(() => PlayAsync(state, cts.Token));
        return state;
    }

    public void Stop(int matchId)
    {
        if (runs.TryRemove(matchId, out var cts)) cts.Cancel();
    }

    async Task PlayAsync(LiveState s, CancellationToken ct)
    {
        var rng = new Random(s.MatchId * 7919);
        var toWin = s.BestOf / 2 + 1;
        // a fixed "strength" per side makes results feel less like coin flips
        var edge = 0.5 + (rng.NextDouble() - 0.5) * 0.25;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(RoundDelay, ct);
                var aWins = rng.NextDouble() < edge;
                var team = aWins ? s.TeamA : s.TeamB;
                var ra = s.RoundsA + (aWins ? 1 : 0);
                var rb = s.RoundsB + (aWins ? 0 : 1);
                var text = string.Format(Plays[rng.Next(Plays.Length)], team);

                // map ends at 13 with a 2-round lead (overtime otherwise)
                var mapOver = (ra >= 13 || rb >= 13) && Math.Abs(ra - rb) >= 2;
                if (!mapOver)
                {
                    s = s with { Version = s.Version + 1, RoundsA = ra, RoundsB = rb, LastEvent = text, ServerTime = Now() };
                    Publish(s);
                    continue;
                }

                var ma = s.MapsA + (ra > rb ? 1 : 0);
                var mb = s.MapsB + (rb > ra ? 1 : 0);
                var winner = ra > rb ? s.TeamA : s.TeamB;
                if (ma == toWin || mb == toWin)
                {
                    s = s with { Version = s.Version + 1, RoundsA = ra, RoundsB = rb, MapsA = ma, MapsB = mb,
                        LastEvent = $"{winner} win {s.MapName} {Math.Max(ra, rb)}-{Math.Min(ra, rb)} and the series {Math.Max(ma, mb)}-{Math.Min(ma, mb)}!",
                        Status = "Completed", ServerTime = Now() };
                    Publish(s);
                    await FinishAsync(s);
                    return;
                }
                s = s with { Version = s.Version + 1, RoundsA = ra, RoundsB = rb, MapsA = ma, MapsB = mb,
                    LastEvent = $"{winner} take {s.MapName} {Math.Max(ra, rb)}-{Math.Min(ra, rb)}", ServerTime = Now() };
                Publish(s);
                await SaveScoreAsync(s.MatchId, ma, mb, MatchStatus.Live);

                await Task.Delay(RoundDelay * 2, ct); // map break
                s = s with { Version = s.Version + 1, MapNumber = s.MapNumber + 1, MapName = Maps[rng.Next(Maps.Length)],
                    RoundsA = 0, RoundsB = 0, LastEvent = "Next map is starting", ServerTime = Now() };
                Publish(s);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { log.LogError(e, "Live match {Id} crashed", s.MatchId); }
        finally { runs.TryRemove(s.MatchId, out _); }
    }

    void Publish(LiveState s)
    {
        states[s.MatchId] = s;
        if (waiters.TryRemove(s.MatchId, out var tcs)) tcs.TrySetResult(s);
        Changed?.Invoke(s);
        _ = hub.Clients.Group(LiveHub.Group(s.MatchId)).SendAsync("state", s);
    }

    /// <summary>Raised when a simulation starts / once a series is over (B8 publishes both to the queue).</summary>
    public event Func<LiveState, Task>? MatchStarted;
    public event Func<LiveState, Task>? MatchFinished;

    async Task FinishAsync(LiveState s)
    {
        await SaveScoreAsync(s.MatchId, s.MapsA, s.MapsB, MatchStatus.Completed);
        if (MatchFinished is { } handler)
            try { await handler(s); }
            catch (Exception e) { log.LogWarning(e, "MatchFinished handler failed for {Id}", s.MatchId); }
    }

    async Task SaveScoreAsync(int matchId, int a, int b, MatchStatus status)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Matches.Where(m => m.Id == matchId).ExecuteUpdateAsync(u => u
            .SetProperty(m => m.ScoreA, a).SetProperty(m => m.ScoreB, b).SetProperty(m => m.Status, status));
    }

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>C11: SignalR hub. Clients join a match group and get "state" pushes.</summary>
public class LiveHub(LiveService live) : Hub
{
    public static string Group(int matchId) => $"match-{matchId}";

    public async Task<LiveState?> Join(int matchId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(matchId));
        return live.Get(matchId);
    }

    public Task Leave(int matchId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(matchId));
}
