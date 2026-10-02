using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;

namespace VctHub.Web.Board;

public record BoardPeer(string Id, string Name, string Color, bool Voice);

class BoardRoom
{
    public string Map { get; set; } = "7eaecc1b-4337-bbf6-6ab9-04b8f06b3319"; // Ascent
    /// <summary>Items are kept as raw JSON: the server doesn't need to understand strokes vs markers.</summary>
    public List<JsonElement> Items { get; } = [];
    public ConcurrentDictionary<string, BoardPeer> Peers { get; } = new();
    public DateTime Touched { get; set; } = DateTime.UtcNow;
    public readonly Lock Gate = new();
}

/// <summary>
/// C18: shared tactical board. Everyone in a room sees strokes, arrows and agent markers appear live,
/// plus each other's cursors. It also relays WebRTC signalling for the voice chat (C15).
/// Rooms live in memory: open /board/any-name and share the link.
/// </summary>
public partial class BoardHub : Hub
{
    const int MaxItems = 3000;
    static readonly ConcurrentDictionary<string, BoardRoom> Rooms = new();
    static readonly string[] Colors = ["#ff4655", "#60ddc0", "#cfb473", "#9d8cff", "#5ab0ff", "#ff8a3d", "#e56bff"];

    string? RoomName => Context.Items["room"] as string;
    BoardRoom Room => Rooms[RoomName ?? throw new HubException("Join a room first")];
    static string Group(string room) => "board-" + room;

    public async Task<object> Join(string room, string? name)
    {
        if (!RoomRegex().IsMatch(room)) throw new HubException("Room name: letters, digits, dashes");
        // forget rooms nobody touched for a day
        foreach (var (k, old) in Rooms) if (old.Peers.IsEmpty && old.Touched < DateTime.UtcNow.AddDays(-1)) Rooms.TryRemove(k, out _);

        var r = Rooms.GetOrAdd(room, _ => new BoardRoom());
        var who = Context.User?.FindFirstValue("display_name") ?? (string.IsNullOrWhiteSpace(name) ? "Guest" : name.Trim()[..Math.Min(name.Trim().Length, 24)]);
        var peer = new BoardPeer(Context.ConnectionId, who, Colors[r.Peers.Count % Colors.Length], false);
        r.Peers[Context.ConnectionId] = peer;
        Context.Items["room"] = room;
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(room));
        await Clients.OthersInGroup(Group(room)).SendAsync("peers", r.Peers.Values);
        lock (r.Gate) return new { me = peer, map = r.Map, items = r.Items.ToList(), peers = r.Peers.Values };
    }

    public Task Add(JsonElement item) => Mutate("add", item, r =>
    {
        if (r.Items.Count >= MaxItems) throw new HubException("Board is full — clear it first");
        r.Items.Add(item.Clone());
    });

    public Task Update(JsonElement item) => Mutate("update", item, r =>
    {
        var id = item.GetProperty("id").GetString();
        var i = r.Items.FindIndex(x => x.GetProperty("id").GetString() == id);
        if (i >= 0) r.Items[i] = item.Clone();
    });

    public Task Remove(string id) => Mutate("remove", id, r => r.Items.RemoveAll(x => x.GetProperty("id").GetString() == id));

    public Task Clear() => Mutate("clear", null, r => r.Items.Clear());

    /// <summary>A valorant-api map id, or a small data: URL of an image someone dropped onto the board.</summary>
    public Task SetMap(string mapId)
    {
        if (mapId.Length > 400_000 || !(MapIdRegex().IsMatch(mapId) || mapId.StartsWith("data:image/jpeg;base64,")))
            throw new HubException("Unsupported map");
        return Mutate("map", mapId, r => { r.Map = mapId; r.Items.Clear(); });
    }

    /// <summary>Unfinished stroke / dragged marker, so others see it being drawn. Not stored.</summary>
    public Task Draft(JsonElement draft) => Clients.OthersInGroup(Group(RoomName!)).SendAsync("draft", Context.ConnectionId, draft);

    public Task Cursor(double x, double y) => Clients.OthersInGroup(Group(RoomName!)).SendAsync("cursor", Context.ConnectionId, x, y);

    // ---------- C15: WebRTC voice. The hub only passes offers/answers/ICE candidates between two peers.

    public async Task Voice(bool on)
    {
        var r = Room;
        if (r.Peers.TryGetValue(Context.ConnectionId, out var p)) r.Peers[Context.ConnectionId] = p with { Voice = on };
        await Clients.Group(Group(RoomName!)).SendAsync("peers", r.Peers.Values);
    }

    public Task Signal(string to, JsonElement payload)
    {
        if (!Room.Peers.ContainsKey(to)) return Task.CompletedTask; // only inside the same room
        return Clients.Client(to).SendAsync("signal", Context.ConnectionId, payload);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (RoomName is { } room && Rooms.TryGetValue(room, out var r))
        {
            r.Peers.TryRemove(Context.ConnectionId, out _);
            await Clients.Group(Group(room)).SendAsync("peers", r.Peers.Values);
            await Clients.Group(Group(room)).SendAsync("left", Context.ConnectionId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    async Task Mutate(string kind, object? payload, Action<BoardRoom> apply)
    {
        var r = Room;
        lock (r.Gate) { apply(r); r.Touched = DateTime.UtcNow; }
        await Clients.OthersInGroup(Group(RoomName!)).SendAsync("change", kind, payload, Context.ConnectionId);
    }

    [GeneratedRegex("^[a-zA-Z0-9-]{1,40}$")] private static partial Regex RoomRegex();
    [GeneratedRegex("^[0-9a-f-]{36}$")] private static partial Regex MapIdRegex();
}
