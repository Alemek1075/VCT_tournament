using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using VctHub.Web.Data;
using VctHub.Web.Models;

namespace VctHub.Web.Services;

public static class Roles
{
    public const string Admin = "Admin";
}

/// <summary>Puts tenant, plan and display name into the auth cookie / token.</summary>
public class AppClaimsFactory(UserManager<AppUser> users, RoleManager<IdentityRole> roles, IOptions<IdentityOptions> options, AppDbContext db)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == user.TenantId);
        id.AddClaim(new Claim(HttpCurrentTenant.Claim, tenant.Id.ToString()));
        id.AddClaim(new Claim("tenant_name", tenant.Name));
        id.AddClaim(new Claim("plan", tenant.Plan.ToString()));
        id.AddClaim(new Claim("display_name", string.IsNullOrEmpty(user.DisplayName) ? user.Email ?? "" : user.DisplayName));
        if (user.AvatarUrl != null) id.AddClaim(new Claim("avatar", user.AvatarUrl));
        return id;
    }
}

public class AccountService(AppDbContext db, UserManager<AppUser> users, IConfiguration config)
{
    /// <summary>New user: joins a workspace by invite code, otherwise gets a fresh one of their own.</summary>
    public async Task<(AppUser? User, IEnumerable<string> Errors)> CreateAsync(
        string email, string displayName, string? password, string? workspace, string? inviteCode, string? avatar = null)
    {
        Tenant? tenant = null;
        if (!string.IsNullOrWhiteSpace(inviteCode))
        {
            tenant = await db.Tenants.FirstOrDefaultAsync(t => t.InviteCode == inviteCode.Trim().ToUpper());
            if (tenant is null) return (null, ["Invite code not found"]);
        }
        tenant ??= await NewTenantAsync(string.IsNullOrWhiteSpace(workspace) ? $"{displayName}'s workspace" : workspace.Trim());

        var user = new AppUser { UserName = email, Email = email, DisplayName = displayName, TenantId = tenant.Id, AvatarUrl = avatar, EmailConfirmed = password == null };
        var result = password == null ? await users.CreateAsync(user) : await users.CreateAsync(user, password);
        if (!result.Succeeded) return (null, result.Errors.Select(e => e.Description));

        if (IsAdminEmail(email)) await users.AddToRoleAsync(user, Roles.Admin);
        return (user, []);
    }

    public async Task<Tenant> NewTenantAsync(string name)
    {
        var baseSlug = Slug.Make(name);
        var slug = baseSlug;
        for (var i = 2; await db.Tenants.AnyAsync(t => t.Slug == slug); i++) slug = $"{baseSlug}-{i}";
        var tenant = new Tenant { Name = name, Slug = slug, InviteCode = NewInviteCode() };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    public bool IsAdminEmail(string email) =>
        (config["ADMIN_EMAILS"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(email, StringComparer.OrdinalIgnoreCase);

    static string NewInviteCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
}

/// <summary>JWT access tokens for the JSON API (Bearer), same claims as the cookie.</summary>
public class TokenService(IConfiguration config)
{
    public const string Issuer = "vct-hub";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    /// <summary>JWT_KEY, or a key derived from other secrets so it is stable per environment.</summary>
    public static SymmetricSecurityKey Key(IConfiguration config)
    {
        var secret = config["JWT_KEY"] ?? "vct-hub-dev:" + (config["GOOGLE_CLIENT_SECRET"] ?? config["SUPABASE_DB_PASSWORD"] ?? "local");
        return new SymmetricSecurityKey(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    public string Create(ClaimsPrincipal principal)
    {
        var claims = principal.Claims.Where(c => c.Type != "AspNet.Identity.SecurityStamp").ToList();
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Issuer,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.Add(Lifetime),
            SigningCredentials = new SigningCredentials(Key(config), SecurityAlgorithms.HmacSha256),
        });
    }
}

/// <summary>Creates roles and an optional seeded admin (used by CI / docker compose for API tests).</summary>
public static class IdentitySeed
{
    public static async Task RunAsync(IServiceProvider sp)
    {
        var roles = sp.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(Roles.Admin)) await roles.CreateAsync(new IdentityRole(Roles.Admin));

        var config = sp.GetRequiredService<IConfiguration>();
        var email = config["SEED_ADMIN_EMAIL"];
        var password = config["SEED_ADMIN_PASSWORD"];
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password)) return;
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        if (await users.FindByEmailAsync(email) is { } existing)
        {
            // keep the seeded account in sync with config (it's only used by local/CI stacks)
            if (!await users.CheckPasswordAsync(existing, password))
            {
                await users.RemovePasswordAsync(existing);
                await users.AddPasswordAsync(existing, password);
            }
            return;
        }
        var (user, _) = await sp.GetRequiredService<AccountService>().CreateAsync(email, "Admin", password, "Admins", null);
        if (user != null && !await users.IsInRoleAsync(user, Roles.Admin)) await users.AddToRoleAsync(user, Roles.Admin);
    }
}
