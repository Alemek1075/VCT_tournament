using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Models;

namespace VctHub.Web.Messaging;

/// <summary>Thin Telegram Bot API client (sendMessage / getUpdates / setWebhook).</summary>
public class TelegramClient(HttpClient http, IConfiguration config)
{
    public string? Token => config["TELEGRAM_BOT_TOKEN"];
    public bool Enabled => !string.IsNullOrWhiteSpace(Token);
    string Url(string method) => $"https://api.telegram.org/bot{Token}/{method}";

    public async Task SendAsync(long chatId, string html, CancellationToken ct = default)
    {
        if (!Enabled) return;
        var res = await http.PostAsJsonAsync(Url("sendMessage"),
            new { chat_id = chatId, text = html, parse_mode = "HTML", disable_web_page_preview = true }, ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Telegram sendMessage {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync(ct)}");
    }

    public async Task<JsonArray> GetUpdatesAsync(long offset, CancellationToken ct)
    {
        var res = await http.GetFromJsonAsync<JsonObject>(Url($"getUpdates?timeout=25&offset={offset}"), ct);
        return res?["result"]?.AsArray() ?? [];
    }

    public Task SetWebhookAsync(string url, string secret, CancellationToken ct) =>
        http.PostAsJsonAsync(Url("setWebhook"), new { url, secret_token = secret, allowed_updates = new[] { "message" } }, ct);

    public Task DeleteWebhookAsync(CancellationToken ct) => http.PostAsync(Url("deleteWebhook"), null, ct);
}

/// <summary>
/// B2: bot commands. Works both via webhook (production) and getUpdates long polling (local dev).
/// /start team_5 (deep link from a team page), /follow fnatic, /unfollow fnatic, /list, /next fnatic, /live
/// </summary>
public class TelegramBot(IServiceScopeFactory scopes, TelegramClient tg, IConfiguration config, ILogger<TelegramBot> log)
{
    string Site => (config["PUBLIC_URL"] ?? config["RENDER_EXTERNAL_URL"] ?? "https://vct-hub.onrender.com").TrimEnd('/');

    public async Task HandleUpdateAsync(JsonNode update, CancellationToken ct)
    {
        var msg = update["message"];
        var chatId = msg?["chat"]?["id"]?.GetValue<long>();
        var text = msg?["text"]?.GetValue<string>()?.Trim();
        if (chatId is null || string.IsNullOrEmpty(text)) return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].Split('@')[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        string reply;
        switch (cmd)
        {
            case "/start" when arg.StartsWith("team_") && int.TryParse(arg[5..], out var tid):
                reply = await FollowAsync(db, chatId.Value, await db.Teams.FindAsync([tid], ct), ct);
                break;
            case "/follow":
                reply = await FollowAsync(db, chatId.Value, await FindTeamAsync(db, arg, ct), ct);
                break;
            case "/unfollow":
            {
                var team = await FindTeamAsync(db, arg, ct);
                if (team is null) { reply = "Team not found. Try /unfollow fnatic"; break; }
                var n = await db.TelegramSubscriptions.Where(s => s.ChatId == chatId && s.TeamId == team.Id).ExecuteDeleteAsync(ct);
                reply = n > 0 ? $"Stopped following <b>{H(team.Name)}</b>." : $"You weren't following {H(team.Name)}.";
                break;
            }
            case "/list":
            {
                var teams = await db.TelegramSubscriptions.Where(s => s.ChatId == chatId).Select(s => s.Team!.Name).ToListAsync(ct);
                reply = teams.Count == 0 ? "You don't follow anyone yet. /follow paper rex" : "You follow:\n" + string.Join("\n", teams.Select(t => "• " + H(t)));
                break;
            }
            case "/next":
            {
                var team = await FindTeamAsync(db, arg, ct);
                if (team is null) { reply = "Which team? /next sentinels"; break; }
                var m = await db.Matches.Include(x => x.TeamA).Include(x => x.TeamB).Include(x => x.Tournament)
                    .Where(x => (x.TeamAId == team.Id || x.TeamBId == team.Id) && x.Status != MatchStatus.Completed)
                    .OrderBy(x => x.ScheduledAt).FirstOrDefaultAsync(ct);
                reply = m is null ? $"No upcoming matches for {H(team.Name)}."
                    : $"<b>{H(m.TeamA!.Name)} vs {H(m.TeamB!.Name)}</b>\n{H(m.Tournament?.Name)} · {H(m.Series)}\n{m.ScheduledAt:ddd MMM d, HH:mm} UTC\n{Site}/Matches/Details/{m.Id}";
                break;
            }
            case "/live":
            {
                var live = await db.Matches.Include(x => x.TeamA).Include(x => x.TeamB)
                    .Where(x => x.Status == MatchStatus.Live).ToListAsync(ct);
                reply = live.Count == 0 ? "Nothing is live right now."
                    : string.Join("\n", live.Select(m => $"🔴 {H(m.TeamA!.Name)} {m.ScoreA}–{m.ScoreB} {H(m.TeamB!.Name)} {Site}/Matches/Details/{m.Id}"));
                break;
            }
            default:
                reply = "VCT Hub alerts 🔔\n/follow &lt;team&gt; — alerts when they play\n/unfollow &lt;team&gt;\n/list — who you follow\n/next &lt;team&gt; — next match\n/live — matches live now";
                break;
        }
        await tg.SendAsync(chatId.Value, reply, ct);
    }

    async Task<string> FollowAsync(AppDbContext db, long chatId, Team? team, CancellationToken ct)
    {
        if (team is null) return "Team not found. Try /follow fnatic";
        if (!await db.TelegramSubscriptions.AnyAsync(s => s.ChatId == chatId && s.TeamId == team.Id, ct))
        {
            db.TelegramSubscriptions.Add(new TelegramSubscription { ChatId = chatId, TeamId = team.Id });
            await db.SaveChangesAsync(ct);
        }
        return $"✅ Following <b>{H(team.Name)}</b>. You'll get a message when their match starts and ends.\n{Site}/team/{team.Slug}";
    }

    static async Task<Team?> FindTeamAsync(AppDbContext db, string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) return null;
        return await db.Teams.FirstOrDefaultAsync(t => EF.Functions.ILike(t.Tag, q) || EF.Functions.ILike(t.Name, q), ct)
            ?? await db.Teams.FirstOrDefaultAsync(t => EF.Functions.ILike(t.Name, $"%{q}%"), ct);
    }

    /// <summary>Queue consumer: tell every follower of either team.</summary>
    public async Task NotifyAsync(string routingKey, string json, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        int matchId;
        string text;
        if (routingKey == "match.finished")
        {
            var e = JsonSerializer.Deserialize<MatchFinished>(json)!;
            matchId = e.MatchId;
            var winner = e.MapsA > e.MapsB ? e.TeamA : e.TeamB;
            text = $"🏁 <b>{H(e.TeamA)} {e.MapsA}–{e.MapsB} {H(e.TeamB)}</b>\n{H(winner)} win the series!\n{Site}/Matches/Details/{e.MatchId}";
        }
        else if (routingKey == "match.started")
        {
            var e = JsonSerializer.Deserialize<MatchStarted>(json)!;
            matchId = e.MatchId;
            text = $"🔴 LIVE: <b>{H(e.TeamA)} vs {H(e.TeamB)}</b>\nWatch the score: {Site}/Matches/Details/{e.MatchId}";
        }
        else return;

        var teamIds = await db.Matches.Where(m => m.Id == matchId).Select(m => new[] { m.TeamAId, m.TeamBId }).FirstOrDefaultAsync(ct) ?? [];
        var chats = await db.TelegramSubscriptions.Where(s => teamIds.Contains(s.TeamId)).Select(s => s.ChatId).Distinct().ToListAsync(ct);
        foreach (var chat in chats)
            await tg.SendAsync(chat, text, ct);
        log.LogInformation("{Key}: notified {N} chats", routingKey, chats.Count);
    }

    static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}

/// <summary>Wires the pieces: live simulator -> bus -> Telegram, plus the bot's update source.</summary>
public class MessagingWorker(IMessageBus bus, TelegramBot bot, TelegramClient tg, Live.LiveService live, IConfiguration config, ILogger<MessagingWorker> log) : BackgroundService
{
    public static string WebhookSecret(IConfiguration c) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("vct-hub:" + c["TELEGRAM_BOT_TOKEN"])))[..32];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        live.MatchStarted += s => bus.PublishAsync("match.started", new MatchStarted(s.MatchId, s.TeamA, s.TeamB, DateTime.UtcNow));
        live.MatchFinished += s => bus.PublishAsync("match.finished", new MatchFinished(s.MatchId, s.TeamA, s.TeamB, s.MapsA, s.MapsB, DateTime.UtcNow));

        try { await bus.SubscribeAsync("vct.telegram", "match.*", (k, body) => bot.NotifyAsync(k, body, ct), ct); }
        catch (Exception e) { log.LogError(e, "Could not start the {Kind} consumer", bus.Kind); }

        if (!tg.Enabled) return;
        var publicUrl = config["PUBLIC_URL"] ?? config["RENDER_EXTERNAL_URL"];
        if (config["TELEGRAM_POLLING"] != "true" && !string.IsNullOrEmpty(publicUrl))
        {
            await tg.SetWebhookAsync($"{publicUrl.TrimEnd('/')}/api/telegram/webhook", WebhookSecret(config), ct);
            log.LogInformation("Telegram webhook set to {Url}", publicUrl);
            return;
        }
        if (config["TELEGRAM_POLLING"] != "true") return;

        // local dev: long-poll getUpdates (only one process may do this per bot)
        await tg.DeleteWebhookAsync(ct);
        long offset = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var u in await tg.GetUpdatesAsync(offset, ct))
                {
                    offset = u!["update_id"]!.GetValue<long>() + 1;
                    await bot.HandleUpdateAsync(u, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { log.LogWarning("getUpdates: {Msg}", e.Message); await Task.Delay(5000, ct); }
        }
    }
}
