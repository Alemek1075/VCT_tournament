using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using VctHub.Web.Api;

namespace VctHub.Tests;

/// <summary>
/// Runs the real app (same Program.cs) against a throwaway Postgres in Docker.
/// Shared by all tests in the collection; each test uses its own users/emails.
/// </summary>
public class AppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@tests.local";
    public const string AdminPassword = "admin-pass-123";

    readonly PostgreSqlContainer pg = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("VCT_SKIP_DOTENV", "1");
        await pg.StartAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", pg.GetConnectionString());
        builder.UseSetting("Seed:RankedAccounts", "0");       // the 1M ladder is not needed here
        builder.UseSetting("SEED_ADMIN_EMAIL", AdminEmail);
        builder.UseSetting("SEED_ADMIN_PASSWORD", AdminPassword);
        builder.UseSetting("ADMIN_EMAILS", AdminEmail);
        builder.UseSetting("JWT_KEY", "test-signing-key-only-for-tests");
        builder.UseSetting("TELEGRAM_BOT_TOKEN", "");
        builder.UseSetting("Cache:Seconds", "0");
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await pg.DisposeAsync();
    }

    /// <summary>Client that doesn't follow redirects, so tests can assert on 302s.</summary>
    public HttpClient Raw() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<string> TokenAsync(string email, string password)
    {
        var res = await Raw().PostAsJsonAsync("/api/auth/token", new { email, password });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<TokenResponse>(Json))!.AccessToken;
    }

    /// <summary>Registers a fresh user through the API and returns a client carrying their Bearer token.</summary>
    public async Task<(HttpClient Client, string Email, string Token)> NewUserAsync(string? inviteCode = null, string? workspace = null)
    {
        var email = $"u{Guid.NewGuid():N}@tests.local";
        var res = await Raw().PostAsJsonAsync("/api/auth/register",
            new { email, password = "user-pass-123", displayName = "Tester", workspace, inviteCode });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<TokenResponse>(Json))!.AccessToken;
        return (Bearer(token), email, token);
    }

    public HttpClient Bearer(string token)
    {
        var c = Raw();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }
}

[CollectionDefinition("app")]
public class AppCollection : ICollectionFixture<AppFactory>;
