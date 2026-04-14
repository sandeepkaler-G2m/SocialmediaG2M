using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using SocialMediaPanel.Services;

namespace SocialMediaPanel.Controllers
{
    public class LinkedInController : Controller
    {
        private readonly AppDbContext _db;
        private readonly LinkedInService _linkedin;
        private readonly IDataProtector _protector;
        private readonly IWebHostEnvironment _env;

        // Cookie-based auth (matches rest of project)
        private string UserId =>
            Request.Cookies.TryGetValue("userId", out var id) && !string.IsNullOrEmpty(id)
                ? id
                : User.Identity?.Name ?? "anonymous";

        public LinkedInController(
            AppDbContext db,
            LinkedInService linkedin,
            IDataProtectionProvider dataProtection,
            IWebHostEnvironment env)
        {
            _db = db;
            _linkedin = linkedin;
            _env = env;
            _protector = dataProtection.CreateProtector("LinkedIn.OAuthState");
        }

        // ══════════════════════════════════════════════════════════════
        // GET /LinkedIn/Index  — main LinkedIn dashboard
        // ══════════════════════════════════════════════════════════════
        public async Task<IActionResult> Index()
        {
            var Userid = HttpContext.Session.GetInt32("UserId");
            var username = HttpContext.Session.GetString("UserEmail");

            var integration = await _db.LinkedInIntegrations
                .FirstOrDefaultAsync(l => l.UserId == Userid.ToString() && l.IsActive);

            var posts = new List<LinkedinPosts>();
            if (integration != null)
            {
                posts = await _db.LinkedInPosts
                    .Where(p => p.UserId == Userid.ToString())
                    .OrderByDescending(p => p.CreatedAt)
                    .Take(20)
                    .ToListAsync();
            }

            ViewBag.Integration = integration;
            ViewBag.Posts = posts;
            ViewBag.Success = TempData["LinkedInSuccess"] as string;
            ViewBag.Error = TempData["LinkedInError"] as string;

            return View();
        }

        // ══════════════════════════════════════════════════════════════
        // GET /LinkedIn/Connect  — start OAuth flow
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        public IActionResult Connect()
        {
            try
            {
                var raw = $"linkedin|{UserId}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                var state = _protector.Protect(raw);
                var url = _linkedin.BuildOAuthUrl(state);
                return Redirect(url);
            }
            catch (Exception ex)
            {
                TempData["LinkedInError"] = "Could not build LinkedIn OAuth URL: " + ex.Message;
                return RedirectToAction("Index");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // GET /LinkedIn/Callback  — OAuth callback from LinkedIn
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Callback(
            string? code, string? state, string? error, string? error_description)
        {
            if (error != null)
            {
                TempData["LinkedInError"] = $"LinkedIn access denied: {error_description ?? error}";
                return RedirectToAction("Index");
            }

            if (!_ValidateState(state, out var stateErr))
            {
                TempData["LinkedInError"] = stateErr;
                return RedirectToAction("Index");
            }

            if (string.IsNullOrEmpty(code))
            {
                TempData["LinkedInError"] = "No authorisation code received from LinkedIn.";
                return RedirectToAction("Index");
            }

            try
            {
                var (accessToken, expiresIn, refreshToken) = await _linkedin.ExchangeCodeAsync(code);
                var (liId, name, picture, email) = await _linkedin.GetProfileAsync(accessToken);

                //var userId = UserId;
                var userId = HttpContext.Session.GetInt32("UserId");
                var username = HttpContext.Session.GetString("UserEmail");

                var existing = await _db.LinkedInIntegrations
                    .FirstOrDefaultAsync(l => l.UserId == userId.ToString());


                if (existing != null)
                {
                    existing.LinkedInUserId = liId;
                    existing.DisplayName = name;
                    existing.ProfilePicture = picture;
                    existing.Email = email;
                    existing.AccessToken = accessToken;
                    existing.RefreshToken = refreshToken ?? existing.RefreshToken;
                    existing.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
                    existing.IsActive = true;
                    existing.DisconnectedAt = null;
                    existing.ConnectedAt = DateTime.UtcNow;
                }
                else
                {
                    _db.LinkedInIntegrations.Add(new LinkedInIntegration
                    {
                        UserId = userId.ToString(),
                        LinkedInUserId = liId,
                        DisplayName = name,
                        ProfilePicture = picture,
                        Email = email,
                        AccessToken = accessToken,
                        RefreshToken = refreshToken,
                        TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
                        IsActive = true,
                        ConnectedAt = DateTime.UtcNow
                    });
                }

                await _db.SaveChangesAsync();

                TempData["LinkedInSuccess"] = $"LinkedIn connected as {name}!";
            }
            catch (Exception ex)
            {
                TempData["LinkedInError"] = "LinkedIn connection failed: " + ex.Message;
            }

            return RedirectToAction("Index");
        }

        // ══════════════════════════════════════════════════════════════
        // POST /LinkedIn/Post  — publish a post
        // ══════════════════════════════════════════════════════════════
        [HttpPost]
        [Route("LinkedIn/Post")]
        public async Task<IActionResult> Post(
            [FromForm] string? text,
            [FromForm] string? articleUrl,
            [FromForm] IFormFile? image)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Json(new { success = false, message = "Post text is required." });

            if (text.Length > 3000)
                return Json(new { success = false, message = "Post exceeds 3000 character limit." });

            var Userid = HttpContext.Session.GetInt32("UserId");
            var username = HttpContext.Session.GetString("UserEmail");

            //var userId = UserId;
            var integration = await _db.LinkedInIntegrations
                .FirstOrDefaultAsync(l => l.UserId == Userid.ToString() && l.IsActive);

            if (integration == null)
                return Json(new { success = false, message = "LinkedIn not connected." });

            // Refresh token if expiring
            var token = await _EnsureFreshTokenAsync(integration);
            if (string.IsNullOrEmpty(token))
                return Json(new { success = false, message = "LinkedIn token expired — please reconnect." });

            // Read image if provided
            string? imageBase64 = null;
            string? imageMime = null;
            if (image != null && image.Length > 0)
            {
                using var ms = new MemoryStream();
                await image.CopyToAsync(ms);
                imageBase64 = Convert.ToBase64String(ms.ToArray());
                imageMime = image.ContentType;
            }

            var record = new LinkedinPosts
            {
                UserId = Userid.ToString(),
                PostText = text,
                ArticleUrl = articleUrl,
                CreatedAt = DateTime.UtcNow
            };

            try
            {
                var postId = await _linkedin.PostAsync(
                    token,
                    integration.LinkedInUserId,
                    text,
                    articleUrl,
                    imageBase64,
                    imageMime
                );

                record.PostId = postId;
                record.Status = "posted";
                record.PostedAt = DateTime.UtcNow;

                _db.LinkedInPosts.Add(record);
                await _db.SaveChangesAsync();

                return Json(new { success = true, message = "Post published successfully!", postId });
            }
            catch (Exception ex)
            {
                record.Status = "failed";
                record.ErrorMessage = ex.Message;
                _db.LinkedInPosts.Add(record);
                await _db.SaveChangesAsync();

                return Json(new { success = false, message = "Post failed: " + ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════════════
        // POST /LinkedIn/Disconnect
        // ══════════════════════════════════════════════════════════════
        [HttpPost]
        [Route("LinkedIn/Disconnect")]
        public async Task<IActionResult> Disconnect()
        {
            //var userId = UserId;
            var userId = HttpContext.Session.GetInt32("UserId");

            var integrations = await _db.LinkedInIntegrations
                .Where(l => l.UserId == userId.ToString() && l.IsActive)
                .ToListAsync();

            foreach (var l in integrations)
            {
                l.IsActive = false;
                l.DisconnectedAt = DateTime.UtcNow;
                l.AccessToken = null;
                l.RefreshToken = null;
            }

            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        // ══════════════════════════════════════════════════════════════
        // GET /LinkedIn/PostHistory  — AJAX list of past posts
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        [Route("LinkedIn/PostHistory")]
        public async Task<IActionResult> PostHistory()
        {
            //var userId = UserId;
            var userId = HttpContext.Session.GetInt32("UserId");

            var posts = await _db.LinkedInPosts
                .Where(p => p.UserId == userId.ToString())
                .OrderByDescending(p => p.CreatedAt)
                .Take(30)
                .Select(p => new {
                    p.Id,
                    p.PostText,
                    p.ArticleUrl,
                    p.Status,
                    p.PostedAt,
                    p.CreatedAt,
                    p.ErrorMessage
                })
                .ToListAsync();

            return Json(new { success = true, data = posts });
        }

        // ══ Private helpers ════════════════════════════════════════════

        private bool _ValidateState(string? state, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(state)) { error = "Missing state parameter."; return false; }
            try
            {
                var raw = _protector.Unprotect(state);
                var parts = raw.Split('|');
                if (parts.Length < 3 || parts[0] != "linkedin") { error = "Invalid state."; return false; }
                if (long.TryParse(parts[2], out var ts))
                {
                    if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts > 600)
                    { error = "OAuth session expired — please try again."; return false; }
                }
                return true;
            }
            catch { error = "Invalid state parameter."; return false; }
        }

        private async Task<string?> _EnsureFreshTokenAsync(LinkedInIntegration integration)
        {
            if (integration.TokenExpiresAt.HasValue &&
                integration.TokenExpiresAt.Value > DateTime.UtcNow.AddMinutes(5))
                return integration.AccessToken;

            if (string.IsNullOrEmpty(integration.RefreshToken))
                return null;

            try
            {
                var (newToken, expiresIn) = await _linkedin.RefreshTokenAsync(integration.RefreshToken);
                integration.AccessToken = newToken;
                integration.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
                await _db.SaveChangesAsync();
                return newToken;
            }
            catch { return null; }
        }
    }
}