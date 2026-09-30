using Microsoft.AspNetCore.Mvc;

namespace VctHub.Web.Controllers;

/// <summary>C21: chat page for the analyst agent.</summary>
public class AgentController : Controller
{
    [HttpGet("/agent")]
    public IActionResult Index()
    {
        ViewData["Title"] = "AI analyst";
        ViewData["Description"] = "Ask about VCT 2026 teams, players and matches. The agent looks things up live.";
        return View();
    }
}
