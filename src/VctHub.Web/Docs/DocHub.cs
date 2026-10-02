using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Services;

namespace VctHub.Web.Docs;

public class Block
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "p";    // p h1 h2 h3 ul ol todo quote code hr
    public string Text { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Checked { get; set; }
}

/// <summary>One edit. Kinds: upsert (text/type of a block, optionally inserted after another), delete, move, title.</summary>
public class DocOp
{
    public string Kind { get; set; } = "";
    public Block? Block { get; set; }
    public string? Id { get; set; }
    public string? After { get; set; }   // insert/move target: null = first
    public string? Title { get; set; }
}

public record Peer(string ConnectionId, string UserId, string Name, string Color);

/// <summary>Live copy of a document while at least one editor has it open.</summary>
class DocSession
{
    public required int DocId { get; init; }
    public required int TenantId { get; init; }
    public string Title { get; set; } = "";
    public List<Block> Blocks { get; set; } = [];
    public long Version { get; set; }
    public bool Dirty { get; set; }
    public ConcurrentDictionary<string, Peer> Peers { get; } = new();
    public readonly Lock Gate = new();

    public void Apply(DocOp op)
    {
        switch (op.Kind)
        {
            case "upsert" when op.Block is { } b:
                var i = Blocks.FindIndex(x => x.Id == b.Id);
                if (i >= 0) { Blocks[i] = b; break; }
                Blocks.Insert(IndexAfter(op.After), b);
                break;
            case "delete":
                Blocks.RemoveAll(x => x.Id == op.Id);
                break;
            case "move":
                var from = Blocks.FindIndex(x => x.Id == op.Id);
                if (from < 0) break;
                var moved = Blocks[from];
                Blocks.RemoveAt(from);
                Blocks.Insert(IndexAfter(op.After), moved);
                break;
            case "title" when op.Title != null:
                Title = op.Title.Length > 120 ? op.Title[..120] : op.Title;
                break;
            default:
                throw new HubException("Unknown op");
        }
        Version++;
        Dirty = true;
    }

    int IndexAfter(string? after) => after == null ? 0 : Blocks.FindIndex(x => x.Id == after) + 1;
}

/// <summary>
/// C13/C14: real-time co-editing. Every op is applied on the server copy (last writer wins per block),
/// then broadcast to the other editors with who made it, so their UI can highlight it.
/// Presence ("who is in which block") is broadcast separately.
/// </summary>
[Authorize]
public class DocHub(DocSessions sessions) : Hub
{
    static string Group(int id) => $"doc-{id}";
    static readonly string[] Colors = ["#ff4655", "#60ddc0", "#cfb473", "#9d8cff", "#5ab0ff", "#ff8a3d"];

    int Tenant => int.Parse(Context.User!.FindFirstValue(HttpCurrentTenant.Claim)!);

    public async Task<object> Join(int docId)
    {
        var s = await sessions.OpenAsync(docId, Tenant) ?? throw new HubException("Document not found");
        var name = Context.User!.FindFirstValue("display_name") ?? "someone";
        var peer = new Peer(Context.ConnectionId, Context.UserIdentifier ?? "", name, Colors[Math.Abs(Context.ConnectionId.GetHashCode()) % Colors.Length]);
        s.Peers[Context.ConnectionId] = peer;
        Context.Items["doc"] = docId;
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(docId));
        await Clients.OthersInGroup(Group(docId)).SendAsync("peers", s.Peers.Values);
        lock (s.Gate) return new { s.Title, s.Blocks, s.Version, me = peer, peers = s.Peers.Values };
    }

    public async Task<long> Op(int docId, DocOp op)
    {
        var s = sessions.Get(docId, Tenant) ?? throw new HubException("Join first");
        long v;
        lock (s.Gate) { s.Apply(op); v = s.Version; }
        s.Peers.TryGetValue(Context.ConnectionId, out var by);
        await Clients.OthersInGroup(Group(docId)).SendAsync("op", op, by, v);
        return v;
    }

    public Task Focus(int docId, string? blockId)
    {
        var s = sessions.Get(docId, Tenant);
        if (s is null || !s.Peers.TryGetValue(Context.ConnectionId, out var by)) return Task.CompletedTask;
        return Clients.OthersInGroup(Group(docId)).SendAsync("focus", by, blockId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items["doc"] is int docId && sessions.Get(docId, Tenant) is { } s)
        {
            s.Peers.TryRemove(Context.ConnectionId, out _);
            await Clients.Group(Group(docId)).SendAsync("peers", s.Peers.Values);
        }
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Keeps open documents in memory and writes dirty ones to the database every second.</summary>
public class DocSessions(IServiceScopeFactory scopes, ILogger<DocSessions> log) : BackgroundService
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly ConcurrentDictionary<int, DocSession> open = new();

    internal DocSession? Get(int docId, int tenantId) =>
        open.TryGetValue(docId, out var s) && s.TenantId == tenantId ? s : null;

    internal async Task<DocSession?> OpenAsync(int docId, int tenantId)
    {
        if (Get(docId, tenantId) is { } existing) return existing;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // explicit tenant check: hubs run outside the request that the query filter normally sees
        var doc = await db.StrategyDocs.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(d => d.Id == docId && d.TenantId == tenantId);
        if (doc is null) return null;
        var s = new DocSession
        {
            DocId = doc.Id, TenantId = doc.TenantId, Title = doc.Title, Version = doc.Version,
            Blocks = JsonSerializer.Deserialize<List<Block>>(doc.BlocksJson, Json) ?? [],
        };
        return open.GetOrAdd(docId, s);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            foreach (var s in open.Values.Where(s => s.Dirty))
            {
                string json, title; long version;
                lock (s.Gate) { json = JsonSerializer.Serialize(s.Blocks, Json); title = s.Title; version = s.Version; s.Dirty = false; }
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    await db.StrategyDocs.IgnoreQueryFilters().Where(d => d.Id == s.DocId)
                        .ExecuteUpdateAsync(u => u.SetProperty(d => d.BlocksJson, json).SetProperty(d => d.Title, title)
                            .SetProperty(d => d.Version, version).SetProperty(d => d.UpdatedAt, DateTime.UtcNow), ct);
                }
                catch (Exception e) { log.LogWarning(e, "Saving doc {Id} failed", s.DocId); s.Dirty = true; }
            }
            // forget documents nobody has open (they're saved)
            foreach (var (id, s) in open)
                if (s.Peers.IsEmpty && !s.Dirty) open.TryRemove(id, out _);
        }
    }
}
