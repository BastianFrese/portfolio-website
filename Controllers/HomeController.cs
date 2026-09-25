using Microsoft.AspNetCore.Mvc;

namespace Portfolio.Controllers;

public class HomeController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("impressum")]
    public IActionResult Impressum() => View();
}