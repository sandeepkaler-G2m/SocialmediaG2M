using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using SocialMediaPanel.Services.PdfMerge;

namespace SocialMediaPanel.Areas.WhatsAppPdf.Controllers
{
    /// <summary>
    /// Admin dashboard for the WhatsApp-PDF Mail-Merge feature: aggregate reports
    /// (how many batches/PDFs generated, across all users) and basic account
    /// management (no self-signup — admin creates every account).
    /// </summary>
    [Area("whatsapppdf")]
    [Route("whatsapppdf/admin")]
    public class PdfMergeAdminController : Controller
    {
        private readonly AppDbContext _db;
        private readonly PdfMergeAuthService _auth;

        public PdfMergeAdminController(AppDbContext db, PdfMergeAuthService auth)
        {
            _db = db;
            _auth = auth;
        }

        private IActionResult? RequireAdmin()
        {
            if (_auth.GetUserId(HttpContext.Session) == null)
                return RedirectToAction("Login", "PdfMergeAuth");
            if (!_auth.IsAdmin(HttpContext.Session))
                // Forbid() invokes ASP.NET Core's authentication ForbidAsync, which
                // needs a configured auth scheme — this app uses custom session auth,
                // not ASP.NET Core authentication, so that throws (same gotcha already
                // documented in Controllers/WebhookController.cs). A plain 403 is enough.
                return StatusCode(403, "Forbidden — admin access required.");
            return null;
        }

        public class ReportsViewModel
        {
            public int TotalUsers { get; set; }
            public int TotalBatches { get; set; }
            public int TotalPdfsGenerated { get; set; }
            public List<PdfMergeBatch> RecentBatches { get; set; } = new();
            public List<UserBreakdown> PerUser { get; set; } = new();
        }

        public class UserBreakdown
        {
            public string Username { get; set; } = "";
            public int BatchCount { get; set; }
            public int RecordCount { get; set; }
        }

        [HttpGet("")]
        [HttpGet("reports")]
        public async Task<IActionResult> Reports()
        {
            var redirect = RequireAdmin();
            if (redirect != null) return redirect;

            var users = await _db.PdfMergeUsers.ToListAsync();
            var batches = await _db.PdfMergeBatches.ToListAsync();
            var totalRecords = await _db.PdfMergeRecords.CountAsync();

            var perUser = users.Select(u => new UserBreakdown
            {
                Username = u.Username,
                BatchCount = batches.Count(b => b.UserId == u.Id),
                RecordCount = batches.Where(b => b.UserId == u.Id).Sum(b => b.TotalRecords)
            }).OrderByDescending(u => u.RecordCount).ToList();

            var vm = new ReportsViewModel
            {
                TotalUsers = users.Count,
                TotalBatches = batches.Count,
                TotalPdfsGenerated = totalRecords,
                RecentBatches = batches.OrderByDescending(b => b.CreatedAt).Take(30).ToList(),
                PerUser = perUser
            };

            ViewBag.UsersById = users.ToDictionary(u => u.Id, u => u.Username);
            return View(vm);
        }

        [HttpGet("users")]
        public async Task<IActionResult> Users()
        {
            var redirect = RequireAdmin();
            if (redirect != null) return redirect;

            var users = await _db.PdfMergeUsers.OrderBy(u => u.Username).ToListAsync();
            return View(users);
        }

        [HttpPost("users")]
        public async Task<IActionResult> CreateUser(string username, string password, string role)
        {
            var redirect = RequireAdmin();
            if (redirect != null) return redirect;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                TempData["Error"] = "Username aur password required hain.";
                return RedirectToAction("Users");
            }

            if (await _db.PdfMergeUsers.AnyAsync(u => u.Username == username))
            {
                TempData["Error"] = "Ye username pehle se hai.";
                return RedirectToAction("Users");
            }

            _db.PdfMergeUsers.Add(new PdfMergeUser
            {
                Username = username.Trim(),
                PasswordHash = _auth.HashPassword(password),
                Role = role == "admin" ? "admin" : "user",
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            TempData["Success"] = $"Account '{username}' ban gaya.";
            return RedirectToAction("Users");
        }
    }
}
