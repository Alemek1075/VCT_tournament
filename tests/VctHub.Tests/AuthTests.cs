using System.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace VctHub.Tests;

/// <summary>QA3: authentication and authorization (C16, B5, B6).</summary>
[Collection("app")]
public class AuthTests(AppFactory app)
{
    static object Team(string name) => new { name, tag = "TST", regionId = 2 };

    // ---------- anonymous access

    [Fact]
    public async Task Anonymous_can_read_the_api()
    {
        var res = await app.Raw().GetAsync("/api/teams?limit=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Anonymous_write_to_api_is_401_not_a_redirect()
    {
        var res = await app.Raw().PostAsJsonAsync("/api/teams", Team("Anon FC"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Null(res.Headers.Location);
    }

    [Theory]
    [InlineData("/Teams/Create")]
    [InlineData("/Players/Edit/1")]
    [InlineData("/premium")]
    [InlineData("/account/workspace")]
    public async Task Anonymous_page_that_needs_login_redirects_to_login(string url)
    {
        var res = await app.Raw().GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith("/account/login?ReturnUrl=", res.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task Anonymous_cannot_start_a_live_simulation()
    {
        var res = await app.Raw().PostAsync("/api/live/1/start", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---------- tokens

    [Fact]
    public async Task Registered_user_gets_a_token_and_their_own_free_workspace()
    {
        var (client, email, _) = await app.NewUserAsync(workspace: "Scouting dept");
        var me = await client.GetFromJsonAsync<JsonObject>("/api/auth/me");
        Assert.Equal(email, me!["email"]!.GetValue<string>());
        Assert.Equal("Scouting dept", me["tenant"]!.GetValue<string>());
        Assert.Equal("Free", me["plan"]!.GetValue<string>());
        Assert.Empty(me["roles"]!.AsArray());
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_look_the_same()
    {
        var wrong = await app.Raw().PostAsJsonAsync("/api/auth/token", new { email = AppFactory.AdminEmail, password = "nope-nope-nope" });
        var unknown = await app.Raw().PostAsJsonAsync("/api/auth/token", new { email = "ghost@tests.local", password = "nope-nope-nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        // no user enumeration: same body text for both
        var a = (await wrong.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.ToString();
        var b = (await unknown.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.ToString();
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Account_locks_after_five_failed_attempts()
    {
        var (_, email, _) = await app.NewUserAsync();
        for (var i = 0; i < 5; i++)
            await app.Raw().PostAsJsonAsync("/api/auth/token", new { email, password = "bad-password-" + i });

        var res = await app.Raw().PostAsJsonAsync("/api/auth/token", new { email, password = "user-pass-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Contains("locked", (await res.Content.ReadAsStringAsync()).ToLowerInvariant());
    }

    [Fact]
    public async Task Tampered_token_is_rejected()
    {
        var (_, _, token) = await app.NewUserAsync();
        var parts = token.Split('.');
        // flip the payload: pretend to be someone else, keep the old signature
        var forged = parts[0] + "." + Base64UrlEncoder.Encode("{\"sub\":\"x\",\"role\":\"Admin\"}") + "." + parts[2];
        var res = await app.Bearer(forged).GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var token = MakeToken("test-signing-key-only-for-tests", DateTime.UtcNow.AddHours(-3), DateTime.UtcNow.AddHours(-1));
        var res = await app.Bearer(token).GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_rejected()
    {
        var token = MakeToken("somebody-elses-key", DateTime.UtcNow, DateTime.UtcNow.AddHours(1));
        var res = await app.Bearer(token).GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Duplicate_email_and_weak_password_are_refused()
    {
        var (_, email, _) = await app.NewUserAsync();
        var dup = await app.Raw().PostAsJsonAsync("/api/auth/register", new { email, password = "another-pass-1", displayName = "Copy" });
        var weak = await app.Raw().PostAsJsonAsync("/api/auth/register", new { email = "weak@tests.local", password = "123", displayName = "Weak" });
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
    }

    // ---------- roles

    [Fact]
    public async Task Member_can_create_but_only_admin_can_delete()
    {
        var (member, _, _) = await app.NewUserAsync();
        var created = await member.PostAsJsonAsync("/api/teams", Team($"Role Test {Guid.NewGuid():N}"[..20]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<int>();

        var byMember = await member.DeleteAsync($"/api/teams/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);

        var admin = app.Bearer(await app.TokenAsync(AppFactory.AdminEmail, AppFactory.AdminPassword));
        var byAdmin = await admin.DeleteAsync($"/api/teams/{id}");
        Assert.Equal(HttpStatusCode.NoContent, byAdmin.StatusCode);
    }

    [Fact]
    public async Task Seeded_admin_has_admin_role_in_token()
    {
        var admin = app.Bearer(await app.TokenAsync(AppFactory.AdminEmail, AppFactory.AdminPassword));
        var me = await admin.GetFromJsonAsync<JsonObject>("/api/auth/me");
        Assert.Contains("Admin", me!["roles"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    // ---------- tenants (B5)

    [Fact]
    public async Task Invite_code_puts_the_new_user_into_the_same_workspace()
    {
        var (owner, _, _) = await app.NewUserAsync(workspace: "Invite test");
        var ownerMe = await owner.GetFromJsonAsync<JsonObject>("/api/auth/me");

        // the invite code is shown on the workspace page; read it through the DB-free route: register page link
        var code = await InviteCodeOf(ownerMe!["tenantId"]!.GetValue<string>());
        var (guest, _, _) = await app.NewUserAsync(inviteCode: code);
        var guestMe = await guest.GetFromJsonAsync<JsonObject>("/api/auth/me");
        Assert.Equal(ownerMe["tenantId"]!.ToString(), guestMe!["tenantId"]!.ToString());
    }

    [Fact]
    public async Task Bad_invite_code_is_refused()
    {
        var res = await app.Raw().PostAsJsonAsync("/api/auth/register",
            new { email = $"x{Guid.NewGuid():N}@tests.local", password = "user-pass-123", displayName = "X", inviteCode = "NOPE0000" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---------- cookie login, open redirect, feature flags (B6)

    [Fact]
    public async Task Login_ignores_external_return_url()
    {
        var (_, email, _) = await app.NewUserAsync();
        var client = app.Raw();
        var res = await PostFormAsync(client, "/account/login?returnUrl=https%3A%2F%2Fevil.example", "/account/login?returnUrl=https%3A%2F%2Fevil.example",
            new() { ["Email"] = email, ["Password"] = "user-pass-123" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Auth_cookie_is_http_only_and_same_site()
    {
        var (_, email, _) = await app.NewUserAsync();
        var res = await PostFormAsync(app.Raw(), "/account/login", "/account/login", new() { ["Email"] = email, ["Password"] = "user-pass-123" });
        var cookie = res.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("vct.auth="));
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=lax", cookie.ToLowerInvariant());
    }

    [Fact]
    public async Task Premium_page_is_locked_on_free_and_opens_after_upgrade()
    {
        var (_, email, _) = await app.NewUserAsync();
        var client = app.CreateClient(); // follows redirects, keeps cookies
        await PostFormAsync(client, "/account/login", "/account/login", new() { ["Email"] = email, ["Password"] = "user-pass-123" });

        var locked = await client.GetStringAsync("/premium");
        Assert.Contains("Upgrade the workspace", locked);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/premium/players.csv")).StatusCode);

        await PostFormAsync(client, "/account/workspace", "/account/plan", new() { ["plan"] = "Premium" });

        var open = await client.GetStringAsync("/premium");
        Assert.Contains("Breakout players", open);
        var csv = await client.GetAsync("/premium/players.csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Form_post_without_antiforgery_token_is_rejected()
    {
        var res = await app.Raw().PostAsync("/account/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = AppFactory.AdminEmail, ["Password"] = AppFactory.AdminPassword }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Telegram_webhook_needs_the_secret_header()
    {
        var res = await app.Raw().PostAsJsonAsync("/api/telegram/webhook", new { update_id = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---------- helpers

    static string MakeToken(string key, DateTime notBefore, DateTime expires) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "vct-hub", Audience = "vct-hub",
            Subject = new ClaimsIdentity([new Claim(ClaimTypes.Name, "intruder"), new Claim(ClaimTypes.Role, "Admin")]),
            NotBefore = notBefore, Expires = expires, IssuedAt = notBefore,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))),
                SecurityAlgorithms.HmacSha256),
        });

    /// <summary>GETs a page, takes its antiforgery token, POSTs the form (like a browser would).</summary>
    static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageUrl, string postUrl, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(pageUrl);
        var doc = new HtmlParser().ParseDocument(html);
        var token = doc.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        fields["__RequestVerificationToken"] = token;
        return await client.PostAsync(postUrl, new FormUrlEncodedContent(fields));
    }

    async Task<string> InviteCodeOf(string tenantId)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VctHub.Web.Data.AppDbContext>();
        return db.Tenants.Single(t => t.Id == int.Parse(tenantId)).InviteCode;
    }
}
