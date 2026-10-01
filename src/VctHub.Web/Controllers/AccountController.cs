using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Models;
using VctHub.Web.Services;

namespace VctHub.Web.Controllers;

public class LoginInput
{
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    public bool Remember { get; set; } = true;
}

public class RegisterInput
{
    [Required, StringLength(60), Display(Name = "Your name")] public string DisplayName { get; set; } = "";
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required, StringLength(100, MinimumLength = 8), DataType(DataType.Password)] public string Password { get; set; } = "";
    [StringLength(60), Display(Name = "New workspace name")] public string? Workspace { get; set; }
    [StringLength(12), Display(Name = "…or invite code")] public string? InviteCode { get; set; }
}

[Route("account")]
public class AccountController(
    SignInManager<AppUser> signIn, UserManager<AppUser> users, AccountService accounts,
    AppDbContext db, IAuthenticationSchemeProvider schemes) : Controller
{
    async Task<bool> GoogleEnabled() => await schemes.GetSchemeAsync("Google") != null;

    [HttpGet("login")]
    public async Task<IActionResult> Login(string? returnUrl)
    {
        ViewBag.ReturnUrl = returnUrl;
        ViewBag.Google = await GoogleEnabled();
        return View(new LoginInput());
    }

    [HttpPost("login"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginInput input, string? returnUrl)
    {
        ViewBag.ReturnUrl = returnUrl;
        ViewBag.Google = await GoogleEnabled();
        if (!ModelState.IsValid) return View(input);
        var result = await signIn.PasswordSignInAsync(input.Email, input.Password, input.Remember, lockoutOnFailure: true);
        if (result.Succeeded) return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
        // same message for "no such user" and "wrong password": don't leak which emails exist
        ModelState.AddModelError("", result.IsLockedOut ? "Too many attempts. Try again in 5 minutes." : "Wrong email or password.");
        return View(input);
    }

    [HttpGet("register")]
    public async Task<IActionResult> Register(string? invite)
    {
        ViewBag.Google = await GoogleEnabled();
        return View(new RegisterInput { InviteCode = invite });
    }

    [HttpPost("register"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterInput input)
    {
        ViewBag.Google = await GoogleEnabled();
        if (!ModelState.IsValid) return View(input);
        var (user, errors) = await accounts.CreateAsync(input.Email, input.DisplayName, input.Password, input.Workspace, input.InviteCode);
        if (user is null)
        {
            foreach (var e in errors) ModelState.AddModelError("", e);
            return View(input);
        }
        await signIn.SignInAsync(user, isPersistent: true);
        TempData["Flash"] = $"Welcome, {user.DisplayName}!";
        return RedirectToAction(nameof(Workspace));
    }

    [HttpPost("logout"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return Redirect("/");
    }

    // ---- Google (OAuth 2.0 / OpenID Connect via ASP.NET external login)

    [HttpPost("external"), ValidateAntiForgeryToken]
    public IActionResult External(string provider, string? returnUrl)
    {
        var redirect = Url.Action(nameof(ExternalCallback), new { returnUrl });
        var props = signIn.ConfigureExternalAuthenticationProperties(provider, redirect);
        return Challenge(props, provider);
    }

    /// <summary>GET entry for Google sign-in, used by the React SPA (it has no antiforgery token to post).</summary>
    [HttpGet("google")]
    public async Task<IActionResult> Google(string? returnUrl)
    {
        if (!await GoogleEnabled()) return RedirectToAction(nameof(Login), new { returnUrl });
        var redirect = Url.Action(nameof(ExternalCallback), new { returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null });
        return Challenge(signIn.ConfigureExternalAuthenticationProperties("Google", redirect), "Google");
    }

    [HttpGet("external-callback")]
    public async Task<IActionResult> ExternalCallback(string? returnUrl, string? remoteError)
    {
        if (remoteError != null)
        {
            TempData["Flash"] = $"Google sign-in failed: {remoteError}";
            return RedirectToAction(nameof(Login));
        }
        var info = await signIn.GetExternalLoginInfoAsync();
        if (info is null) return RedirectToAction(nameof(Login));

        var result = await signIn.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: true, bypassTwoFactor: true);
        if (!result.Succeeded)
        {
            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            if (email is null) { TempData["Flash"] = "Google did not share an email address."; return RedirectToAction(nameof(Login)); }
            var user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                var name = info.Principal.FindFirstValue(ClaimTypes.GivenName) ?? info.Principal.FindFirstValue(ClaimTypes.Name) ?? email;
                (user, var errors) = await accounts.CreateAsync(email, name, null, null, null, info.Principal.FindFirstValue("picture"));
                if (user is null) { TempData["Flash"] = string.Join(" ", errors); return RedirectToAction(nameof(Login)); }
            }
            await users.AddLoginAsync(user, info);
            await signIn.SignInAsync(user, isPersistent: true, info.LoginProvider);
        }
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/account/workspace");
    }

    // ---- workspace (B5) and plan (B6)

    [Authorize, HttpGet("workspace")]
    public async Task<IActionResult> Workspace()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        var tenant = await db.Tenants.Include(t => t.Members).FirstAsync(t => t.Id == user.TenantId);
        ViewBag.Docs = await db.StrategyDocs.CountAsync(); // tenant filter applies
        ViewBag.IsAdmin = User.IsInRole(Roles.Admin);
        return View(tenant);
    }

    /// <summary>Demo billing: flips the workspace plan. A real app would do this from a payment webhook.</summary>
    [Authorize, HttpPost("plan"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePlan(Plan plan)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        var tenant = await db.Tenants.FirstAsync(t => t.Id == user.TenantId);
        tenant.Plan = plan;
        await db.SaveChangesAsync();
        await signIn.RefreshSignInAsync(user); // new "plan" claim in the cookie right away
        TempData["Flash"] = plan == Plan.Premium ? "Workspace upgraded to Premium ✨" : "Workspace is on the Free plan now";
        return RedirectToAction(nameof(Workspace));
    }

    [Authorize, HttpPost("join"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Join(string inviteCode)
    {
        var user = await users.GetUserAsync(User);
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.InviteCode == inviteCode.Trim().ToUpper());
        if (user is null || tenant is null) { TempData["Flash"] = "Invite code not found"; return RedirectToAction(nameof(Workspace)); }
        user.TenantId = tenant.Id;
        await users.UpdateAsync(user);
        await signIn.RefreshSignInAsync(user);
        TempData["Flash"] = $"You joined {tenant.Name}";
        return RedirectToAction(nameof(Workspace));
    }

    [HttpGet("denied")]
    public IActionResult Denied() => View();
}
