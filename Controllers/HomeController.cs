using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SocialMediaPanel.Models;

namespace SocialMediaPanel.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View();
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
