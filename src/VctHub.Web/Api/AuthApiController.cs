using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using VctHub.Web.Models;
using VctHub.Web.Services;

namespace VctHub.Web.Api;

public record TokenRequest([Required, EmailAddress] string Email, [Required] string Password);
public record RegisterRequest([Required, EmailAddress] string Email, [Required, StringLength(100, MinimumLength = 8)] string Password,
    [Required, StringLength(60)] string DisplayName, string? Workspace, string? InviteCode);
public record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);

/// <summary>C16: get a Bearer token for the API (password grant, same accounts as the website).</summary>
[Route("api/auth"), OutputCache(NoStore = true)]
public class AuthApiController(UserManager<AppUser> users, SignInManager<AppUser> signIn, AccountService accounts, TokenService tokens) : ApiBase
{
    [HttpPost("token"), AllowAnonymous]
    [ProducesResponseType<TokenResponse>(200), ProducesResponseType(400), ProducesResponseType(401)]
    public async Task<IActionResult> Token(TokenRequest req)
    {
        var user = await users.FindByEmailAsync(req.Email);
        if (user is null) return Unauthorized401();
        var check = await signIn.CheckPasswordSignInAsync(user, req.Password, lockoutOnFailure: true);
        if (!check.Succeeded) return Unauthorized401(check.IsLockedOut ? "Account locked for 5 minutes" : null);
        var principal = await signIn.CreateUserPrincipalAsync(user);
        return Ok(new TokenResponse(tokens.Create(principal), "Bearer", (int)TokenService.Lifetime.TotalSeconds));
    }

    [HttpPost("register"), AllowAnonymous]
    [ProducesResponseType<TokenResponse>(201), ProducesResponseType(400)]
    public async Task<IActionResult> Register(RegisterRequest req)
    {
        var (user, errors) = await accounts.CreateAsync(req.Email, req.DisplayName, req.Password, req.Workspace, req.InviteCode);
        if (user is null)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]> { [""] = errors.ToArray() }));
        var principal = await signIn.CreateUserPrincipalAsync(user);
        return StatusCode(201, new TokenResponse(tokens.Create(principal), "Bearer", (int)TokenService.Lifetime.TotalSeconds));
    }

    /// <summary>Swap the website cookie (e.g. after Google sign-in) for a Bearer token. Same-origin only:
    /// the cookie is SameSite=Lax and there is no CORS, so another site can't read the answer.</summary>
    [HttpPost("session-token"), Authorize(AuthenticationSchemes = "Identity.Application")]
    [ProducesResponseType<TokenResponse>(200), ProducesResponseType(401)]
    public async Task<IActionResult> SessionToken()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Unauthorized401("Not signed in");
        var principal = await signIn.CreateUserPrincipalAsync(user);
        return Ok(new TokenResponse(tokens.Create(principal), "Bearer", (int)TokenService.Lifetime.TotalSeconds));
    }

    /// <summary>Who am I (works with cookie or Bearer).</summary>
    [HttpGet("me"), Authorize]
    public IActionResult Me() => Ok(new
    {
        id = User.FindFirstValue(ClaimTypes.NameIdentifier),
        email = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue(ClaimTypes.Name),
        name = User.FindFirstValue("display_name"),
        tenantId = User.FindFirstValue(HttpCurrentTenant.Claim),
        tenant = User.FindFirstValue("tenant_name"),
        plan = User.FindFirstValue("plan"),
        roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value),
    });

    ObjectResult Unauthorized401(string? detail = null) =>
        Problem(statusCode: 401, title: "Unauthorized", detail: detail ?? "Wrong email or password");
}
