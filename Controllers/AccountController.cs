using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using SocialMediaPanel.Models.ViewModels;
using System;
using System.Security.Cryptography;
using System.Text;

namespace SocialMediaPanel.Controllers
{
    public class AccountController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IMemoryCache _cache;
        private readonly SocialMediaPanel.Services.AuditLogService _audit;

        public AccountController(AppDbContext context, IMemoryCache cache, SocialMediaPanel.Services.AuditLogService audit)
        {
            _context = context;
            _cache = cache;
            _audit = audit;
        }

        // ─── AUDIT LOG ───────────────────────────────────────────

        [HttpGet]
        public IActionResult AuditLog()
        {
            if (HttpContext.Session.GetInt32("UserId") == null)
                return RedirectToAction("Login");

            return View(_audit.GetRecent());
        }

        // ─── LOGIN ──────────────────────────────────────────────

        [HttpGet]
        public IActionResult Login()
        {
            if (HttpContext.Session.GetInt32("UserId") != null)
                return RedirectToAction("Index", "Dashboard");

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var hashedPassword = HashPassword(model.Password);

            // LINQ query with Entity Framework Core
            var user = await _context.Users
                .Where(u => u.Email == model.Email && u.Password == hashedPassword)
                .FirstOrDefaultAsync();

            if (user == null)
            {
                ModelState.AddModelError("", "Invalid email or password.");
                return View(model);
            }

            // Store session
            HttpContext.Session.SetInt32("UserId", user.Id);
            HttpContext.Session.SetString("UserName", user.Name);
            HttpContext.Session.SetString("UserEmail", user.Email);
            HttpContext.Session.SetString("CompanyName", user.CompanyName ?? "");

            return RedirectToAction("Index", "Dashboard");
        }

        // ─── REGISTER ────────────────────────────────────────────

        [HttpGet]
        public IActionResult Register()
        {
            if (HttpContext.Session.GetInt32("UserId") != null)
                return RedirectToAction("Index", "Dashboard");

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            // Check if email already exists using LINQ
            bool emailExists = await _context.Users
                .AnyAsync(u => u.Email == model.Email);

            if (emailExists)
            {
                ModelState.AddModelError("Email", "This email is already registered.");
                return View(model);
            }

            var user = new User
            {
                Name = model.Name,
                Email = model.Email,
                Password = HashPassword(model.Password),
                CompanyName = model.CompanyName,
                CompanySize = model.CompanySize,
                CompanyType = model.CompanyType,
                CompanyAddress = model.CompanyAddress,
              //  CreatedAt = DateTime.UtcNow
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            // Auto login after register
            HttpContext.Session.SetInt32("UserId", user.Id);
            HttpContext.Session.SetString("UserName", user.Name);
            HttpContext.Session.SetString("UserEmail", user.Email);

            return RedirectToAction("Index", "Dashboard");
        }

        // ─── FORGOT PASSWORD ─────────────────────────────────────

        [HttpGet]
        public IActionResult ForgotPassword()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(string email)
        {
            var user = await _context.Users.Where(u => u.Email == email).FirstOrDefaultAsync();
            if (user == null)
            {
                ViewBag.Error = "No account found with that email.";
                return View();
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _cache.Set($"pwreset:{token}", user.Id, TimeSpan.FromMinutes(30));

            ViewBag.ResetLink = Url.Action("ResetPassword", "Account", new { token }, Request.Scheme);
            ViewBag.Info = "This app has no email server configured yet, so here is your reset link directly:";
            return View();
        }

        // ─── RESET PASSWORD ──────────────────────────────────────

        [HttpGet]
        public IActionResult ResetPassword(string token)
        {
            if (string.IsNullOrEmpty(token) || !_cache.TryGetValue($"pwreset:{token}", out int _))
            {
                ViewBag.Error = "This reset link is invalid or has expired.";
                return View();
            }

            ViewBag.Token = token;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(string token, string password, string confirmPassword)
        {
            if (string.IsNullOrEmpty(token) || !_cache.TryGetValue($"pwreset:{token}", out int userId))
            {
                ViewBag.Error = "This reset link is invalid or has expired.";
                return View();
            }

            if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            {
                ViewBag.Error = "Password must be at least 6 characters.";
                ViewBag.Token = token;
                return View();
            }

            if (password != confirmPassword)
            {
                ViewBag.Error = "Passwords do not match.";
                ViewBag.Token = token;
                return View();
            }

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                ViewBag.Error = "Account no longer exists.";
                return View();
            }

            user.Password = HashPassword(password);
            await _context.SaveChangesAsync();
            _cache.Remove($"pwreset:{token}");

            ViewBag.Success = true;
            return View();
        }

        // ─── LOGOUT ──────────────────────────────────────────────

        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Login");
        }

        // ─── HELPER ──────────────────────────────────────────────

        private static string HashPassword(string password)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(bytes);
        }
    }
}