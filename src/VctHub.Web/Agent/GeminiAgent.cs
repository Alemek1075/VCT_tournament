using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VctHub.Web.Agent;

public record ChatMessage(string Role, string Text);
public record AgentEvent(string Type, object Data);

/// <summary>
/// C21: tool-using chat agent on Gemini (free tier). Streams everything it does:
/// "thinking" (thought summaries), "tool_call", "tool_result", "text" deltas, then "done".
/// </summary>
public class GeminiAgent(HttpClient http, IConfiguration config, ILogger<GeminiAgent> log)
{
    const int MaxSteps = 6;
    // lite models answer in seconds on the free tier; the big ones are often overloaded (503) or slow
    static readonly string[] Fallbacks = ["gemini-3.5-flash-lite", "gemini-3.1-flash-lite", "gemini-3.5-flash", "gemini-flash-latest"];

    const string SystemPrompt = """
        You are the VCT Hub analyst: an assistant for the Valorant Champions Tour 2026 on the VCT Hub website.
        Always use the tools for facts about teams, players, matches and stats; never invent numbers.
        Use as few tool calls as possible: top_players and get_team already include each player's stats,
        so don't look players up one by one after them. Call independent tools in parallel.
        Keep answers short and concrete, cite the stats you used, and link pages with the relative "url" fields
        from tool results as markdown links. If a tool returns an error, say so plainly.
        When asked to write a game plan or notes, write them and save with create_strategy_doc (only if the user asks to save).
        Today is {date}.
        """;

    static readonly JsonArray Tools = JsonNode.Parse("""
        [{ "functionDeclarations": [
          { "name": "search", "description": "Full-text search over teams, players, events and matches (typo tolerant).",
            "parameters": { "type": "object", "properties": { "query": { "type": "string" }, "type": { "type": "string", "enum": ["team","player","event","match"] } }, "required": ["query"] } },
          { "name": "get_team", "description": "Team profile: roster with season stats, W-L record, last results, upcoming matches.",
            "parameters": { "type": "object", "properties": { "name": { "type": "string", "description": "Team name or tag, e.g. FNATIC or PRX" } }, "required": ["name"] } },
          { "name": "get_player", "description": "Player profile with season stats and per-event stats.",
            "parameters": { "type": "object", "properties": { "nickname": { "type": "string" } }, "required": ["nickname"] } },
          { "name": "list_matches", "description": "Matches by status: upcoming, live or results. Optionally for one team.",
            "parameters": { "type": "object", "properties": { "status": { "type": "string", "enum": ["upcoming","live","results"] }, "team": { "type": "string" }, "limit": { "type": "integer" } }, "required": ["status"] } },
          { "name": "top_players", "description": "Leaderboard of players by a stat (min 200 rounds).",
            "parameters": { "type": "object", "properties": { "stat": { "type": "string", "enum": ["rating","acs","kd","adr","hs"] }, "role": { "type": "string", "enum": ["Duelist","Initiator","Controller","Sentinel"] }, "limit": { "type": "integer" } }, "required": ["stat"] } },
          { "name": "create_strategy_doc", "description": "Save a markdown document into the signed-in user's workspace. Only when the user asks to save.",
            "parameters": { "type": "object", "properties": { "title": { "type": "string" }, "markdown": { "type": "string" } }, "required": ["title","markdown"] } }
        ] }]
        """)!.AsArray();

    public bool Enabled => !string.IsNullOrWhiteSpace(config["GEMINI_API_KEY"]);

    public async Task RunAsync(IReadOnlyList<ChatMessage> history, VctTools tools, int? tenantId, string user,
        Func<AgentEvent, Task> emit, CancellationToken ct)
    {
        var contents = new JsonArray();
        foreach (var m in history.TakeLast(20))
            contents.Add(new JsonObject { ["role"] = m.Role == "user" ? "user" : "model", ["parts"] = new JsonArray(new JsonObject { ["text"] = m.Text }) });

        for (var step = 0; step < MaxSteps; step++)
        {
            var body = new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = SystemPrompt.Replace("{date}", DateTime.UtcNow.ToString("yyyy-MM-dd")) }) },
                ["contents"] = contents.DeepClone(),
                // last step: no tools, so the model has to answer with what it already found
                ["tools"] = step < MaxSteps - 1 ? Tools.DeepClone() : new JsonArray(),
                ["generationConfig"] = new JsonObject { ["thinkingConfig"] = new JsonObject { ["includeThoughts"] = true }, ["temperature"] = 0.4 },
            };

            var modelParts = new JsonArray();
            var calls = new List<JsonObject>();
            await foreach (var part in StreamAsync(body, ct))
            {
                modelParts.Add(part.DeepClone()); // echoed back verbatim: Gemini needs its thoughtSignature on the next turn
                if (part["functionCall"] is JsonObject fc) calls.Add(fc);
                else if (part["text"]?.GetValue<string>() is { Length: > 0 } text)
                    await emit(new AgentEvent(part["thought"]?.GetValue<bool>() == true ? "thinking" : "text", text));
            }
            contents.Add(new JsonObject { ["role"] = "model", ["parts"] = modelParts });
            if (calls.Count == 0) break;

            var responses = new JsonArray();
            foreach (var call in calls)
            {
                var name = call["name"]!.GetValue<string>();
                var args = call["args"] as JsonObject ?? new JsonObject();
                await emit(new AgentEvent("tool_call", new { name, args = JsonSerializer.Deserialize<JsonElement>(args.ToJsonString()) }));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                object result;
                try { result = await CallToolAsync(tools, name, args, tenantId, user, ct); }
                catch (Exception e) { result = new { error = e.Message }; }
                var json = JsonSerializer.SerializeToNode(result, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                await emit(new AgentEvent("tool_result", new { name, ms = sw.ElapsedMilliseconds, result = JsonSerializer.Deserialize<JsonElement>(json.ToJsonString()) }));
                var fr = new JsonObject { ["name"] = name, ["response"] = new JsonObject { ["result"] = json } };
                if (call["id"] is { } id) fr["id"] = id.DeepClone();
                responses.Add(new JsonObject { ["functionResponse"] = fr });
            }
            contents.Add(new JsonObject { ["role"] = "user", ["parts"] = responses });
        }
    }

    static Task<object> CallToolAsync(VctTools t, string name, JsonObject a, int? tenantId, string user, CancellationToken ct)
    {
        string S(string k) => a[k]?.GetValue<string>() ?? "";
        string? O(string k) => a[k]?.GetValue<string>();
        int I(string k, int d) => a[k] is JsonValue v && v.TryGetValue<int>(out var i) ? i : a[k] is JsonValue v2 && v2.TryGetValue<double>(out var dd) ? (int)dd : d;
        return name switch
        {
            "search" => t.SearchAsync(S("query"), O("type"), ct),
            "get_team" => t.GetTeamAsync(S("name"), ct),
            "get_player" => t.GetPlayerAsync(S("nickname"), ct),
            "list_matches" => t.ListMatchesAsync(O("status") ?? "upcoming", O("team"), I("limit", 10), ct),
            "top_players" => t.TopPlayersAsync(O("stat") ?? "rating", O("role"), I("limit", 10), ct),
            "create_strategy_doc" => t.CreateStrategyDocAsync(tenantId, user, S("title"), S("markdown"), ct),
            _ => Task.FromResult<object>(new { error = $"Unknown tool {name}" }),
        };
    }

    /// <summary>Gemini streamGenerateContent (SSE). Tries the next model when one is overloaded.</summary>
    async IAsyncEnumerable<JsonObject> StreamAsync(JsonObject body, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var models = new[] { config["GEMINI_MODEL"] }.Concat(Fallbacks).Where(m => !string.IsNullOrEmpty(m)).Distinct().ToList();
        HttpResponseMessage? res = null;
        foreach (var model in models)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse")
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Add("x-goog-api-key", config["GEMINI_API_KEY"]);
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(25));
            try { res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Gemini {Model} did not answer in 25 s, trying the next one", model);
                res = null;
                continue;
            }
            if (res.IsSuccessStatusCode) break;
            var err = await res.Content.ReadAsStringAsync(ct);
            log.LogWarning("Gemini {Model} -> {Status}: {Err}", model, (int)res.StatusCode, err.Length > 300 ? err[..300] : err);
            if (res.StatusCode is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests or HttpStatusCode.NotFound))
                throw new InvalidOperationException($"Gemini error {(int)res.StatusCode}");
            res = null;
        }
        if (res is null) throw new InvalidOperationException("All Gemini models are busy right now (free tier). Try again in a minute.");

        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            var chunk = JsonNode.Parse(line[6..]);
            if (chunk?["candidates"]?[0]?["content"]?["parts"] is not JsonArray parts) continue;
            foreach (var p in parts.OfType<JsonObject>()) yield return p;
        }
    }
}
