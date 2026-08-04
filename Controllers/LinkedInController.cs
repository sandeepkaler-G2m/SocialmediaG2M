using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using SocialMediaPanel.Services;
using System.Text.Json;

namespace SocialMediaPanel.Controllers
{
    public class LinkedInController : Controller
    {
        private readonly AppDbContext _db;
        private readonly LinkedInService _linkedin;
        private readonly IDataProtector _protector;
        private readonly IWebHostEnvironment _env;

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
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var integration = await _db.LinkedInIntegrations
                .FirstOrDefaultAsync(l => l.UserId == userId.ToString() && l.IsActive);

            var posts = new List<LinkedinPosts>();

            // Scope flags — drive which sections render in the view
            var grantedScopes = integration?.GrantedScopes?
    .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
    ?? Array.Empty<string>();

            bool hasProfile = grantedScopes.Contains("openid") || grantedScopes.Contains("profile") || grantedScopes.Contains("r_liteprofile");
            bool hasPosting = grantedScopes.Contains("w_member_social");
            bool hasOrgAccess = grantedScopes.Contains("r_organization_admin");
            bool hasLeadsAccess = grantedScopes.Contains("r_marketing_leadgen_automation") || grantedScopes.Contains("r_ads_leadgen_automation");
            bool hasAdsAccess = grantedScopes.Contains("r_ads");
            bool hasEventsAccess = grantedScopes.Contains("r_events") || grantedScopes.Contains("rw_events");

            List<(string Urn, string Name, string? LogoUrl)> organizations = new();
            List<Dictionary<string, JsonElement>> leads = new();

            if (integration != null)
            {
                posts = await _db.LinkedInPosts
                    .Where(p => p.UserId == userId.ToString())
                    .OrderByDescending(p => p.CreatedAt)
                    .Take(20)
                    .ToListAsync();

                var token = await _EnsureFreshTokenAsync(integration);

                if (!string.IsNullOrEmpty(token))
                {
                    if (hasOrgAccess)
                    {
                        try { organizations = await _linkedin.GetOrganizationsAsync(token); }
                        catch (Exception ex) { TempData["LinkedInError"] = "Could not load organization pages: " + ex.Message; }
                    }

                    if (hasLeadsAccess && organizations.Any())
                    {
                        try { leads = await _linkedin.GetLeadsAsync(token, organizations.First().Urn); }
                        catch (Exception ex) { TempData["LinkedInError"] = "Could not load leads: " + ex.Message; }
                    }
                }
            }

            ViewBag.Integration = integration;
            ViewBag.Posts = posts;
            ViewBag.HasProfile = hasProfile;
            ViewBag.HasPosting = hasPosting;
            ViewBag.HasOrgAccess = hasOrgAccess;
            ViewBag.HasLeadsAccess = hasLeadsAccess;
            ViewBag.HasAdsAccess = hasAdsAccess;
            ViewBag.HasEventsAccess = hasEventsAccess;
            ViewBag.Organizations = organizations;
            ViewBag.Leads = leads;
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
                var (accessToken, expiresIn, refreshToken, grantedScopes) = await _linkedin.ExchangeCodeAsync(code);
                var (liId, name, picture, email) = await _linkedin.GetProfileAsync(accessToken);

                var userId = HttpContext.Session.GetInt32("UserId");

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
                    existing.GrantedScopes = grantedScopes;
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
                        GrantedScopes = grantedScopes,
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

            var integration = await _db.LinkedInIntegrations
                .FirstOrDefaultAsync(l => l.UserId == Userid.ToString() && l.IsActive);

            if (integration == null)
                return Json(new { success = false, message = "LinkedIn not connected." });

            var token = await _EnsureFreshTokenAsync(integration);
            if (string.IsNullOrEmpty(token))
                return Json(new { success = false, message = "LinkedIn token expired — please reconnect." });

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

            if (string.IsNullOrWhiteSpace(state))
            {
                error = "Missing state.";
                return false;
            }

            var sessionState = HttpContext.Session.GetString("LinkedInOAuthState");

            if (string.IsNullOrWhiteSpace(sessionState))
            {
                error = "OAuth session expired.";
                return false;
            }

            if (!string.Equals(state, sessionState, StringComparison.Ordinal))
            {
                error = "Invalid state.";
                return false;
            }

            // Remove after successful validation
            HttpContext.Session.Remove("LinkedInOAuthState");

            return true;
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