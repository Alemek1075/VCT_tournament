using Microsoft.AspNetCore.Mvc;

namespace VctHub.Web.Controllers;

/// <summary>C18 tactical board. /board picks a random room, /board/{room} joins one.</summary>
public class BoardController : Controller
{
    [HttpGet("/board")]
    public IActionResult Index() => Redirect("/board/" + Guid.NewGuid().ToString("N")[..8]);

    [HttpGet("/board/{room:regex(^[[a-zA-Z0-9-]]{{1,40}}$)}")]
    public IActionResult Room(string room)
    {
        ViewData["Title"] = "Tactical board";
        ViewData["Description"] = "Draw strats on Valorant maps together, live, with voice.";
        return View("Board", room);
    }
}
