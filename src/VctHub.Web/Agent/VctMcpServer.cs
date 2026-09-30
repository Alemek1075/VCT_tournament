using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol.Server;
using VctHub.Web.Services;

namespace VctHub.Web.Agent;

/// <summary>
/// C22: MCP server at /mcp (Streamable HTTP). Five read tools, one write tool.
/// Reads are anonymous; create_strategy_doc needs "Authorization: Bearer &lt;token from /api/auth/token&gt;".
/// </summary>
[McpServerToolType]
public class VctMcpTools(VctTools tools, IHttpContextAccessor http)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static string J(object o) => JsonSerializer.Serialize(o, Json);

    [McpServerTool(Name = "search", ReadOnly = true), Description("Full-text search (typo tolerant) over VCT 2026 teams, players, events and matches.")]
    public async Task<string> Search(
        [Description("What to look for, e.g. 'fnatic', 'aspas', 'champions'")] string query,
        [Description("Optional filter: team, player, event or match")] string? type = null,
        CancellationToken ct = default) => J(await tools.SearchAsync(query, type, ct));

    [McpServerTool(Name = "get_team", ReadOnly = true), Description("Team profile: roster with season stats, W-L record, last 5 results, upcoming matches.")]
    public async Task<string> GetTeam([Description("Team name or tag, e.g. 'Paper Rex' or 'PRX'")] string name, CancellationToken ct = default) =>
        J(await tools.GetTeamAsync(name, ct));

    [McpServerTool(Name = "get_player", ReadOnly = true), Description("Player profile: role, main agent, season stats and stats per event.")]
    public async Task<string> GetPlayer([Description("Player nickname, e.g. 'aspas'")] string nickname, CancellationToken ct = default) =>
        J(await tools.GetPlayerAsync(nickname, ct));

    [McpServerTool(Name = "list_matches", ReadOnly = true), Description("VCT matches by status (upcoming, live, results), optionally for one team.")]
    public async Task<string> ListMatches(
        [Description("upcoming | live | results")] string status = "upcoming",
        [Description("Optional team name or tag")] string? team = null,
        [Description("How many (1-30)")] int limit = 10,
        CancellationToken ct = default) => J(await tools.ListMatchesAsync(status, team, limit, ct));

    [McpServerTool(Name = "top_players", ReadOnly = true), Description("Leaderboard of players by rating, acs, kd, adr or hs (players with 200+ rounds).")]
    public async Task<string> TopPlayers(
        [Description("rating | acs | kd | adr | hs")] string stat = "rating",
        [Description("Optional role: Duelist, Initiator, Controller, Sentinel")] string? role = null,
        [Description("How many (1-25)")] int limit = 10,
        CancellationToken ct = default) => J(await tools.TopPlayersAsync(stat, role, limit, ct));

    [McpServerTool(Name = "create_strategy_doc", Destructive = false), Description("Save a markdown strategy document into the caller's VCT Hub workspace. Requires a Bearer token.")]
    public async Task<string> CreateStrategyDoc(
        [Description("Document title")] string title,
        [Description("Markdown body: # headings, - lists, - [ ] todos, > quotes")] string markdown,
        CancellationToken ct = default)
    {
        var user = http.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
            return J(new { error = "Not signed in. Get a token with POST /api/auth/token and send it as 'Authorization: Bearer <token>'." });
        var tenant = int.TryParse(user.FindFirstValue(HttpCurrentTenant.Claim), out var t) ? t : (int?)null;
        return J(await tools.CreateStrategyDocAsync(tenant, user.FindFirstValue("display_name") ?? "mcp", title, markdown, ct));
    }
}
