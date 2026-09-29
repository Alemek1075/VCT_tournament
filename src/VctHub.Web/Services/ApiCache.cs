using Microsoft.AspNetCore.OutputCaching;

namespace VctHub.Web.Services;

/// <summary>
/// B9: GET responses of the JSON API are cached (Redis when REDIS_URL is set, process memory otherwise).
/// Any successful write (API or MVC form) evicts the "api" tag, so nobody reads stale data after a change.
/// </summary>
public static class ApiCache
{
    public const string Policy = "api";
    public const string Tag = "api";

    public static IServiceCollection AddApiCache(this IServiceCollection services, IConfiguration config)
    {
        var redis = RedisConfig(config);
        if (redis != null)
            services.AddStackExchangeRedisOutputCache(o =>
            {
                o.Configuration = redis;
                o.InstanceName = "vct:";
            });

        services.AddOutputCache(o =>
            o.AddPolicy(Policy, b => b
                .Expire(TimeSpan.FromSeconds(config.GetValue("Cache:Seconds", 60)))
                .SetVaryByQuery("*")
                .Tag(Tag)));
        return services;
    }

    public static IApplicationBuilder UseApiCache(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            var isApiRead = HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/api");
            if (isApiRead)
                // the output cache adds "Age" only when it answers from the cache
                ctx.Response.OnStarting(() =>
                {
                    ctx.Response.Headers["X-Cache"] = ctx.Response.Headers.ContainsKey("Age") ? "HIT" : "MISS";
                    return Task.CompletedTask;
                });

            await next();

            var isWrite = !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method);
            if (isWrite && ctx.Response.StatusCode < 400)
            {
                await ctx.RequestServices.GetRequiredService<IOutputCacheStore>().EvictByTagAsync(Tag, default);
                ctx.RequestServices.GetRequiredService<SearchIndexer>().RequestReindex(); // B4: keep the search index fresh
            }
        });
        app.UseOutputCache();
        return app;
    }

    /// <summary>Render gives "redis://user:pass@host:port"; StackExchange.Redis wants "host:port,password=...".</summary>
    public static string? RedisConfig(IConfiguration config)
    {
        var url = config["REDIS_URL"];
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!url.StartsWith("redis", StringComparison.OrdinalIgnoreCase)) return url;
        var u = new Uri(url);
        var cs = $"{u.Host}:{(u.Port > 0 ? u.Port : 6379)},abortConnect=false";
        var pass = u.UserInfo.Split(':').LastOrDefault();
        if (!string.IsNullOrEmpty(pass)) cs += $",password={Uri.UnescapeDataString(pass)}";
        if (url.StartsWith("rediss", StringComparison.OrdinalIgnoreCase)) cs += ",ssl=true";
        return cs;
    }
}
