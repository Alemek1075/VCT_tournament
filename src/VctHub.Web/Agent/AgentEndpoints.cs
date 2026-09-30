using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using VctHub.Web.Services;

namespace VctHub.Web.Agent;

public record ChatRequest(List<ChatMessage> Messages);

public static class AgentEndpoints
{
    public const string RatePolicy = "agent";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddAgent(this IServiceCollection services)
    {
        services.AddScoped<VctTools>();
        services.AddHttpClient<GeminiAgent>(c => c.Timeout = TimeSpan.FromMinutes(2));
        // the Gemini free tier is small: keep one visitor from eating the whole quota
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.AddPolicy(RatePolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });
        return services;
    }

    public static void MapAgent(this WebApplication app)
    {
        // C21: Server-Sent Events over a POST (the browser reads it with fetch + ReadableStream)
        app.MapPost("/api/agent/chat", async (ChatRequest req, HttpContext ctx, GeminiAgent agent, VctTools tools, ILogger<GeminiAgent> log) =>
        {
            if (req.Messages is not { Count: > 0 } || req.Messages.Any(m => m.Text.Length > 4000))
                return Results.BadRequest(new { error = "messages: 1..20 items, 4000 chars max each" });
            if (!agent.Enabled) return Results.Problem("GEMINI_API_KEY is not configured", statusCode: 503);

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var ct = ctx.RequestAborted;

            async Task Emit(AgentEvent e)
            {
                await ctx.Response.WriteAsync($"event: {e.Type}\ndata: {JsonSerializer.Serialize(e.Data, Json)}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }

            var tenant = int.TryParse(ctx.User.FindFirstValue(HttpCurrentTenant.Claim), out var t) ? t : (int?)null;
            var user = ctx.User.FindFirstValue("display_name") ?? "guest";
            try
            {
                await agent.RunAsync(req.Messages, tools, tenant, user, Emit, ct);
                await Emit(new AgentEvent("done", new { }));
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                log.LogWarning(e, "Agent run failed");
                await Emit(new AgentEvent("error", new { message = e.Message }));
            }
            return Results.Empty;
        }).RequireRateLimiting(RatePolicy).DisableAntiforgery();
    }
}
