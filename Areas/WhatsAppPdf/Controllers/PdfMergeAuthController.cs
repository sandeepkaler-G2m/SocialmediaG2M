using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Services.PdfMerge;

namespace SocialMediaPanel.Areas.WhatsAppPdf.Controllers
{
    /// <summary>
    /// Login/logout for the standalone WhatsApp-PDF Mail-Merge feature. Completely
    /// separate from the main app's Controllers/AccountController — own credentials
    /// table (PdfMergeUser), own session-key namespace (PdfMergeAuthService).
    /// </summary>
    [Area("whatsapppdf")]
    [Route("whatsapppdf/login")]
    public class PdfMergeAuthController : Controller
    {
        private readonly AppDbContext _db;
        private readonly PdfMergeAuthService _auth;

        public PdfMergeAuthController(AppDbContext db, PdfMergeAuthService auth)
        {
            _db = db;
            _auth = auth;
        }

        [HttpGet("")]
        public IActionResult Login()
        {
            if (_auth.GetUserId(HttpContext.Session) != null)
                return RedirectToAction("Index", "PdfMergeDashboard");

            return View();
        }

        [HttpPost("")]
        public async Task<IActionResult> LoginPost(string username, string password)
        {
            var user = await _db.PdfMergeUsers.FirstOrDefaultAsync(u => u.Username == username);
            if (user == null || !_auth.VerifyPassword(password, user.PasswordHash))
            {
                ViewBag.Error = "Username ya password galat hai.";
                return View("Login");
            }

            _auth.SignIn(HttpContext.Session, user);
            return RedirectToAction("Index", "PdfMergeDashboard");
        }

        [HttpPost("~/whatsapppdf/logout")]
        public IActionResult Logout()
        {
            _auth.SignOut(HttpContext.Session);
            return RedirectToAction("Login");
        }
    }
}
