using Microsoft.AspNetCore.Mvc;

namespace VctHub.Web.Controllers;

/// <summary>F6: canvas aim trainer.</summary>
public class AimController : Controller
{
    [HttpGet("/aim")]
    public IActionResult Index()
    {
        ViewData["Title"] = "Aim trainer";
        ViewData["Description"] = "Gridshot and flick drills in the browser. 30 seconds, how many can you hit?";
        return View();
    }
}
