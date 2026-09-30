using System.Security.Claims;

namespace VctHub.Web.Services;

public interface ICurrentTenant
{
    int? TenantId { get; }
}

/// <summary>Tenant of the signed-in user, taken from the "tenant_id" claim (cookie or JWT).</summary>
public class HttpCurrentTenant(IHttpContextAccessor http) : ICurrentTenant
{
    public const string Claim = "tenant_id";

    public int? TenantId =>
        int.TryParse(http.HttpContext?.User.FindFirstValue(Claim), out var id) ? id : null;
}
