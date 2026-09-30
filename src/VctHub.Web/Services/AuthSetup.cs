using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.FeatureManagement;
using Microsoft.IdentityModel.Tokens;
using VctHub.Web.Data;
using VctHub.Web.Models;

namespace VctHub.Web.Services;

public static class AuthSetup
{
    public const string Bearer = JwtBearerDefaults.AuthenticationScheme;
    public const string AdminPolicy = "Admin";
    public const string Smart = "smart";

    public static IServiceCollection AddVctAuth(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentTenant, HttpCurrentTenant>();
        services.AddScoped<AccountService>();
        services.AddSingleton<TokenService>();

        services.AddIdentity<AppUser, IdentityRole>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 8;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<AppClaimsFactory>();

        services.ConfigureApplicationCookie(o =>
        {
            o.LoginPath = "/account/login";
            o.AccessDeniedPath = "/account/denied";
            o.Cookie.Name = "vct.auth";
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.SlidingExpiration = true;
            o.ExpireTimeSpan = TimeSpan.FromDays(7);
            // the JSON API answers 401/403 instead of redirecting to an HTML login page
            o.Events.OnRedirectToLogin = ctx => ApiOrRedirect(ctx.HttpContext, ctx.RedirectUri, 401);
            o.Events.OnRedirectToAccessDenied = ctx => ApiOrRedirect(ctx.HttpContext, ctx.RedirectUri, 403);
        });

        // one entry scheme: Bearer when the request carries a token, the Identity cookie otherwise,
        // so pages redirect to /account/login and API calls get a clean 401
        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = Smart;
            o.DefaultAuthenticateScheme = Smart;
            o.DefaultChallengeScheme = Smart;
            o.DefaultForbidScheme = Smart;
        });
        auth.AddPolicyScheme(Smart, "Cookie or Bearer", o => o.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? Bearer : IdentityConstants.ApplicationScheme);
        if (!string.IsNullOrWhiteSpace(config["GOOGLE_CLIENT_ID"]))
            auth.AddGoogle(o =>
            {
                o.ClientId = config["GOOGLE_CLIENT_ID"]!;
                o.ClientSecret = config["GOOGLE_CLIENT_SECRET"]!;
                o.SignInScheme = IdentityConstants.ExternalScheme;
                o.ClaimActions.MapJsonKey("picture", "picture");
            });
        auth.AddJwtBearer(Bearer, o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = TokenService.Issuer,
                ValidAudience = TokenService.Issuer,
                IssuerSigningKey = TokenService.Key(config),
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = ClaimTypes.Name,
                RoleClaimType = ClaimTypes.Role,
            };
        });

        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder(Smart).RequireAuthenticatedUser().Build())
            .AddPolicy(AdminPolicy, p => p.AddAuthenticationSchemes(Smart).RequireRole(Roles.Admin));

        // B6: feature flags evaluated against the tenant's plan
        services.AddFeatureManagement().AddFeatureFilter<PlanFeatureFilter>();
        return services;
    }

    static Task ApiOrRedirect(HttpContext ctx, string redirect, int status)
    {
        if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/hubs"))
        {
            ctx.Response.StatusCode = status;
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(redirect);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Reads (a) nothing to log in for GET pages and API reads, (b) login for create/edit,
/// (c) Admin for delete. Applied by convention so no action can be forgotten.
/// </summary>
public class WriteAccessConvention : IApplicationModelConvention
{
    static readonly string[] MvcWriteActions = ["Create", "Edit", "Delete", "DeleteConfirmed"];
    static readonly string[] CrudControllers = ["Teams", "Players", "Tournaments", "Matches"];

    public void Apply(ApplicationModel app)
    {
        foreach (var c in app.Controllers)
        foreach (var a in c.Actions)
        {
            var isApi = c.ControllerType.Namespace?.EndsWith(".Api") == true;
            var verbs = a.Attributes.OfType<Microsoft.AspNetCore.Mvc.Routing.IActionHttpMethodProvider>().SelectMany(x => x.HttpMethods).ToList();
            var isDelete = verbs.Contains("DELETE") || a.ActionName is "Delete" or "DeleteConfirmed";

            if (isApi && verbs.Any(v => v is "POST" or "PUT" or "DELETE"))
                a.Filters.Add(isDelete ? new AuthorizeFilter(AuthSetup.AdminPolicy) : new AuthorizeFilter());
            else if (!isApi && CrudControllers.Contains(c.ControllerName) && MvcWriteActions.Contains(a.ActionName))
                a.Filters.Add(isDelete ? new AuthorizeFilter(AuthSetup.AdminPolicy) : new AuthorizeFilter());
        }
    }
}

/// <summary>"Plan" feature filter: enabled when the signed-in user's tenant is on the given plan.</summary>
[FilterAlias("Plan")]
public class PlanFeatureFilter(IHttpContextAccessor http) : IFeatureFilter
{
    public Task<bool> EvaluateAsync(FeatureFilterEvaluationContext context)
    {
        var required = context.Parameters["Plan"] ?? nameof(Plan.Premium);
        var plan = http.HttpContext?.User.FindFirstValue("plan");
        return Task.FromResult(string.Equals(plan, required, StringComparison.OrdinalIgnoreCase));
    }
}

public static class Features
{
    public const string PremiumStats = "PremiumStats";
    public const string CsvExport = "CsvExport";
}
