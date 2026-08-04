using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SocialMediaPanel.Models;

namespace SocialMediaPanel.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            // No Views/Home/Index.cshtml exists (never did) — this 500'd on
            // any hit. Nothing in the app links here; the real entry point is
            // Account/Login (see Program.cs default route), so just send
            // visitors there instead of crashing.
            return RedirectToAction("Login", "Account");
        }
        public IActionResult INProgress()
        {
            return View();
        }

        public IActionResult sand()
        {
            return View();
        }


    }
}
