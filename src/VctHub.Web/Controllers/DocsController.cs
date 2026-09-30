using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;
using VctHub.Web.Docs;
using VctHub.Web.Models;

namespace VctHub.Web.Controllers;

/// <summary>C12: strategy docs of the signed-in user's workspace (the tenant filter hides everyone else's).</summary>
[Authorize, Route("docs")]
public class DocsController(AppDbContext db) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Strategy docs";
        return View(await db.StrategyDocs.AsNoTracking().OrderByDescending(d => d.UpdatedAt).ToListAsync());
    }

    [HttpPost(""), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string? title, int? matchId)
    {
        var blocks = new List<Block>
        {
            new() { Id = Id(), Type = "p", Text = "Type / markdown: # heading, - list, 1. numbered, [] todo, > quote, ``` code, --- divider." },
            new() { Id = Id(), Type = "h2", Text = "Attack" },
            new() { Id = Id(), Type = "todo", Text = "Default on A: smoke heaven + CT" },
            new() { Id = Id(), Type = "h2", Text = "Defense" },
            new() { Id = Id(), Type = "ul", Text = "Stack B after they lose pistol" },
        };
        var doc = new StrategyDoc
        {
            TenantId = int.Parse(User.FindFirst(Services.HttpCurrentTenant.Claim)!.Value),
            Title = string.IsNullOrWhiteSpace(title) ? "Game plan" : title.Trim(),
            BlocksJson = JsonSerializer.Serialize(blocks, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            UpdatedBy = User.FindFirst("display_name")?.Value,
        };
        db.StrategyDocs.Add(doc);
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Edit), new { id = doc.Id });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Edit(int id)
    {
        var doc = await db.StrategyDocs.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
        if (doc is null) return NotFound(); // another tenant's doc looks exactly like a missing one
        ViewData["Title"] = doc.Title;
        return View("Editor", doc);
    }

    [HttpPost("{id:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var n = await db.StrategyDocs.Where(d => d.Id == id).ExecuteDeleteAsync();
        return n == 0 ? NotFound() : RedirectToAction(nameof(Index));
    }

    /// <summary>Download as a .md file.</summary>
    [HttpGet("{id:int}.md")]
    public async Task<IActionResult> Markdown(int id)
    {
        var doc = await db.StrategyDocs.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
        if (doc is null) return NotFound();
        var blocks = JsonSerializer.Deserialize<List<Block>>(doc.BlocksJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        return File(Encoding.UTF8.GetBytes(ToMarkdown(blocks)), "text/markdown", $"{Data.Slug.Make(doc.Title)}.md");
    }

    /// <summary>No account needed: the document lives only in this browser's localStorage.</summary>
    [AllowAnonymous, HttpGet("local")]
    public IActionResult Local()
    {
        ViewData["Title"] = "Local notes";
        return View("Editor", new StrategyDoc { Id = 0, Title = "My notes" });
    }

    public static string ToMarkdown(IEnumerable<Block> blocks)
    {
        var sb = new StringBuilder();
        var n = 0;
        foreach (var b in blocks)
        {
            n = b.Type == "ol" ? n + 1 : 0;
            sb.AppendLine(b.Type switch
            {
                "h1" => "# " + b.Text, "h2" => "## " + b.Text, "h3" => "### " + b.Text,
                "ul" => "- " + b.Text, "ol" => $"{n}. " + b.Text,
                "todo" => (b.Checked ? "- [x] " : "- [ ] ") + b.Text,
                "quote" => "> " + b.Text, "code" => "```\n" + b.Text + "\n```", "hr" => "---",
                _ => b.Text,
            });
            if (b.Type is "p" or "h1" or "h2" or "h3" or "code" or "quote") sb.AppendLine();
        }
        return sb.ToString();
    }

    static string Id() => Guid.NewGuid().ToString("N")[..10];
}
