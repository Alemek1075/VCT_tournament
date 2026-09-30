using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;

namespace VctHub.Web.Live;

/// <summary>
/// C10: the same live state delivered four ways over plain HTTP/WS, so they can be compared:
/// polling (+ETag for the adaptive client), long polling, Server-Sent Events and a raw WebSocket.
/// SignalR (C11) lives in <see cref="LiveHub"/>.
/// </summary>
public static class LiveEndpoints
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapLive(this WebApplication app)
    {
        var api = app.MapGroup("/api/live").WithTags("Live");

        api.MapGet("/", (LiveService live) => live.All.OrderByDescending(s => s.ServerTime));
        // lets the comparison page estimate the client/server clock offset before measuring latency
        api.MapGet("/clock", () => Results.Ok(new { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));

        // 1+2. polling / adaptive polling. The ETag lets a poller get a tiny 304 when nothing changed.
        api.MapGet("/{id:int}", (int id, HttpContext ctx, LiveService live) =>
        {
            var s = live.Get(id);
            if (s is null) return Results.NotFound();
            var etag = $"\"{s.Version}\"";
            ctx.Response.Headers.ETag = etag;
            ctx.Response.Headers.CacheControl = "no-cache";
            return ctx.Request.Headers.IfNoneMatch == etag ? Results.StatusCode(304) : Results.Ok(s);
        });

        // 3. long polling: the request hangs until there is something newer than `since` (or 25 s pass)
        api.MapGet("/{id:int}/wait", async (int id, long since, LiveService live, CancellationToken ct) =>
        {
            var s = await live.WaitAsync(id, since, TimeSpan.FromSeconds(25), ct);
            return s is null ? Results.NoContent() : Results.Ok(s);
        });

        // 4. Server-Sent Events: one response that never ends, one "data:" line per change
        api.MapGet("/{id:int}/sse", async (int id, HttpContext ctx, LiveService live, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var queue = Channel.CreateUnbounded<LiveState>();
            void OnChange(LiveState s) { if (s.MatchId == id) queue.Writer.TryWrite(s); }
            live.Changed += OnChange;
            try
            {
                if (live.Get(id) is { } first) queue.Writer.TryWrite(first);
                await foreach (var s in queue.Reader.ReadAllAsync(ct))
                {
                    await ctx.Response.WriteAsync($"id: {s.Version}\ndata: {JsonSerializer.Serialize(s, Json)}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
            finally { live.Changed -= OnChange; }
        }).ExcludeFromDescription();

        api.MapPost("/{id:int}/start", async (int id, LiveService live) =>
        {
            try { return Results.Ok(await live.StartAsync(id)); }
            catch (KeyNotFoundException e) { return Results.Problem(e.Message, statusCode: 404); }
        }).RequireAuthorization();
        api.MapPost("/{id:int}/stop", (int id, LiveService live) => { live.Stop(id); return Results.NoContent(); }).RequireAuthorization();

        // 5. raw WebSocket: server pushes a JSON frame per change, client can send "ping"
        app.Map("/ws/live/{id:int}", async (int id, HttpContext ctx, LiveService live) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var queue = Channel.CreateUnbounded<LiveState>();
            void OnChange(LiveState s) { if (s.MatchId == id) queue.Writer.TryWrite(s); }
            live.Changed += OnChange;
            var ct = ctx.RequestAborted;
            try
            {
                if (live.Get(id) is { } first) queue.Writer.TryWrite(first);
                var reader = ReadLoop(ws, ct); // drains client frames so close/ping are handled
                await foreach (var s in queue.Reader.ReadAllAsync(ct))
                {
                    if (ws.State != WebSocketState.Open) break;
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(s, Json);
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                    if (reader.IsCompleted) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally { live.Changed -= OnChange; }
        });
    }

    static async Task ReadLoop(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[256];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                    return;
                }
                if (Encoding.UTF8.GetString(buf, 0, r.Count) == "ping")
                    await ws.SendAsync("pong"u8.ToArray(), WebSocketMessageType.Text, true, ct);
            }
        }
        catch { /* client went away */ }
    }
}
