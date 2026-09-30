using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace VctHub.Web.Models;

public enum Plan { Free, Premium }

/// <summary>A signed-in user. Belongs to exactly one tenant (organisation workspace).</summary>
public class AppUser : IdentityUser
{
    [StringLength(60)] public string DisplayName { get; set; } = "";
    [StringLength(400)] public string? AvatarUrl { get; set; }
    public int TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// B5: a workspace (e.g. a team's coaching staff). Private docs and boards belong to a tenant and are
/// invisible to everyone else. The plan (B6) is per tenant, like in any SaaS.
/// </summary>
public class Tenant
{
    public int Id { get; set; }
    [Required, StringLength(60)] public string Name { get; set; } = "";
    [Required, StringLength(70)] public string Slug { get; set; } = "";
    /// <summary>Share this code to let someone join the workspace.</summary>
    [Required, StringLength(12)] public string InviteCode { get; set; } = "";
    public Plan Plan { get; set; } = Plan.Free;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<AppUser> Members { get; set; } = [];
}

/// <summary>Marker for rows that live inside one tenant; a global query filter hides the rest.</summary>
public interface ITenantScoped
{
    int TenantId { get; set; }
}

/// <summary>C12: Notion-like strategy document (blocks stored as JSON), private to the tenant.</summary>
public class StrategyDoc : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    [Required, StringLength(120)] public string Title { get; set; } = "Untitled";
    /// <summary>JSON array of blocks: [{ id, type, text, checked? }]</summary>
    public string BlocksJson { get; set; } = "[]";
    public long Version { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [StringLength(60)] public string? UpdatedBy { get; set; }
}
