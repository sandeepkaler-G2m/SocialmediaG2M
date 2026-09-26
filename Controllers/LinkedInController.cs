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
        private readonly IConfiguration _config;

        private string UserId =>
            Request.Cookies.TryGetValue("userId", out var id) && !string.IsNullOrEmpty(id)
                ? id
                : User.Identity?.Name ?? "anonymous";

        public LinkedInController(
            AppDbContext db,
            LinkedInService linkedin,
            IDataProtectionProvider dataProtection,
            IWebHostEnvironment env,
            IConfiguration config)
        {
            _db = db;
            _linkedin = linkedin;
            _env = env;
            _config = config;
            _protector = dataProtection.CreateProtector("LinkedIn.OAuthState");
        }

        // Picks which connected LinkedIn account is "current": an explicit
        // ?id= wins (must belong to this user), else the marked default,
        // else just the first active one. Same pattern as WhatsApp's
        // multi-number support.
        private async Task<LinkedInIntegration?> ResolveIntegrationAsync(int? id, string userId)
        {
            if (id.HasValue)
            {
                var picked = await _db.LinkedInIntegrations.FirstOrDefaultAsync(l => l.Id == id && l.UserId == userId && l.IsActive);
                if (picked != null) return picked;
            }
            return await _db.LinkedInIntegrations.Where(l => l.UserId == userId && l.IsActive)
                .OrderByDescending(l => l.IsDefault).ThenBy(l => l.Id)
                .FirstOrDefaultAsync();
        }

        // ══════════════════════════════════════════════════════════════
        // GET /LinkedIn/Index  — main LinkedIn dashboard
        // ══════════════════════════════════════════════════════════════
        public async Task<IActionResult> Index(int? id)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var allIntegrations = await _db.LinkedInIntegrations
                .Where(l => l.UserId == userId.ToString() && l.IsActive)
                .OrderByDescending(l => l.IsDefault).ThenBy(l => l.Id)
                .ToListAsync();
            ViewBag.AllIntegrations = allIntegrations;

            var integration = id.HasValue
                ? allIntegrations.FirstOrDefault(l => l.Id == id)
                : allIntegrations.FirstOrDefault(l => l.IsDefault) ?? allIntegrations.FirstOrDefault();

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
                    .Where(p => p.UserId == userId.ToString() && p.LinkedInIntegrationId == integration.Id)
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

                // Matched by (UserId + LinkedInUserId) — reconnecting the
                // SAME LinkedIn member updates that row; a DIFFERENT member
                // becomes a new row, so one panel user can connect multiple
                // LinkedIn accounts instead of the second overwriting the first.
                var existing = await _db.LinkedInIntegrations
                    .FirstOrDefaultAsync(l => l.UserId == userId.ToString() && l.LinkedInUserId == liId);

                var hadAnyBefore = await _db.LinkedInIntegrations.AnyAsync(l => l.UserId == userId.ToString() && l.IsActive);

                if (existing != null)
                {
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
                        IsDefault = !hadAnyBefore, // first-ever account is default automatically
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
            [FromForm] IFormFile? image,
            [FromForm] int? id)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Json(new { success = false, message = "Post text is required." });

            if (text.Length > 3000)
                return Json(new { success = false, message = "Post exceeds 3000 character limit." });

            var Userid = HttpContext.Session.GetInt32("UserId");

            var integration = await ResolveIntegrationAsync(id, Userid.ToString());

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
                LinkedInIntegrationId = integration.Id,
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
        // POST /LinkedIn/Disconnect — id omitted disconnects the default
        // account (back-compat with the old single-account call sites)
        // ══════════════════════════════════════════════════════════════
        [HttpPost]
        [Route("LinkedIn/Disconnect")]
        public async Task<IActionResult> Disconnect(int? id)
        {
            var userId = HttpContext.Session.GetInt32("UserId");

            var integration = id.HasValue
                ? await _db.LinkedInIntegrations.FirstOrDefaultAsync(l => l.Id == id && l.UserId == userId.ToString())
                : await ResolveIntegrationAsync(null, userId.ToString());

            if (integration != null)
            {
                integration.IsActive = false;
                integration.IsDefault = false;
                integration.DisconnectedAt = DateTime.UtcNow;
                integration.AccessToken = null;
                integration.RefreshToken = null;
                await _db.SaveChangesAsync();

                // Promote another connected account to default if one exists.
                var another = await _db.LinkedInIntegrations.FirstOrDefaultAsync(l => l.UserId == userId.ToString() && l.IsActive);
                if (another != null) { another.IsDefault = true; await _db.SaveChangesAsync(); }
            }

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Json(new { success = true });

            return RedirectToAction("Settings");
        }

        // ══════════════════════════════════════════════════════════════
        // POST /LinkedIn/SetDefault
        // ══════════════════════════════════════════════════════════════
        [HttpPost]
        [Route("LinkedIn/SetDefault")]
        public async Task<IActionResult> SetDefault(int id)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var integrations = await _db.LinkedInIntegrations.Where(l => l.UserId == userId.ToString() && l.IsActive).ToListAsync();
            foreach (var i in integrations) i.IsDefault = (i.Id == id);
            await _db.SaveChangesAsync();

            return RedirectToAction("Index", new { id });
        }

        // ══════════════════════════════════════════════════════════════
        // GET /LinkedIn/PostHistory  — AJAX list of past posts
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        [Route("LinkedIn/PostHistory")]
        public async Task<IActionResult> PostHistory(int? id)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            var integration = await ResolveIntegrationAsync(id, userId.ToString());
            if (integration == null) return Json(new { success = false, message = "LinkedIn not connected." });

            var posts = await _db.LinkedInPosts
                .Where(p => p.UserId == userId.ToString() && p.LinkedInIntegrationId == integration.Id)
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

        // ══════════════════════════════════════════════════════════════
        // SETTINGS — manage connected accounts
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        [Route("LinkedIn/Settings")]
        public async Task<IActionResult> Settings()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var integrations = await _db.LinkedInIntegrations
                .Where(l => l.UserId == userId.ToString() && l.IsActive)
                .OrderByDescending(l => l.IsDefault).ThenBy(l => l.Id)
                .ToListAsync();

            return View(integrations);
        }

        // ══════════════════════════════════════════════════════════════
        // LEADS — Lead Sync API
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        [Route("LinkedIn/Leads")]
        public async Task<IActionResult> Leads(int? id)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var integration = await ResolveIntegrationAsync(id, userId.ToString());
            ViewBag.Integration = integration;
            ViewBag.AllIntegrations = await _db.LinkedInIntegrations.Where(l => l.UserId == userId.ToString() && l.IsActive).ToListAsync();

            List<(string Urn, string Name, string? LogoUrl)> organizations = new();
            if (integration != null)
            {
                var token = await _EnsureFreshTokenAsync(integration);
                if (!string.IsNullOrEmpty(token))
                {
                    try { organizations = await _linkedin.GetOrganizationsAsync(token); }
                    catch (Exception ex) { TempData["LinkedInError"] = "Could not load organization pages: " + ex.Message; }
                }
            }
            ViewBag.Organizations = organizations;

            var subscriptions = integration != null
                ? await _db.LinkedInLeadSubscriptions.Where(s => s.LinkedInIntegrationId == integration.Id && s.Active).ToListAsync()
                : new List<LinkedInLeadSubscription>();
            ViewBag.Subscriptions = subscriptions;

            var leads = await _db.LinkedInLeads.OrderByDescending(l => l.CreatedAt).Take(100).ToListAsync();
            ViewBag.Success = TempData["LinkedInSuccess"] as string;
            ViewBag.Error = TempData["LinkedInError"] as string;

            return View(leads);
        }

        [HttpPost]
        [Route("LinkedIn/RegisterLeadWebhook")]
        public async Task<IActionResult> RegisterLeadWebhook(int? id, string organizationUrn)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var integration = await ResolveIntegrationAsync(id, userId.ToString());
            if (integration == null)
            {
                TempData["LinkedInError"] = "Connect LinkedIn first.";
                return RedirectToAction("Leads");
            }
            if (string.IsNullOrWhiteSpace(organizationUrn))
            {
                TempData["LinkedInError"] = "Select an organization first.";
                return RedirectToAction("Leads");
            }

            var token = await _EnsureFreshTokenAsync(integration);
            if (string.IsNullOrEmpty(token))
            {
                TempData["LinkedInError"] = "LinkedIn token expired — please reconnect.";
                return RedirectToAction("Leads");
            }

            var webhookUrl = (_config["AppBaseUrl"] ?? "").TrimEnd('/') + "/linkedin/webhook";

            var sub = new LinkedInLeadSubscription
            {
                UserId = userId.ToString(),
                LinkedInIntegrationId = integration.Id,
                OwnerUrn = organizationUrn,
                WebhookUrl = webhookUrl,
                Status = "pending"
            };

            try
            {
                var subId = await _linkedin.RegisterLeadNotificationAsync(token, organizationUrn, webhookUrl);
                sub.LinkedInSubscriptionId = subId;
                sub.Status = "registered";
                TempData["LinkedInSuccess"] = $"Lead webhook registered for {organizationUrn} → {webhookUrl}";
            }
            catch (Exception ex)
            {
                sub.Status = "failed";
                sub.ErrorMessage = ex.Message;
                TempData["LinkedInError"] = "Webhook registration failed: " + ex.Message;
            }

            _db.LinkedInLeadSubscriptions.Add(sub);
            await _db.SaveChangesAsync();

            return RedirectToAction("Leads", new { id = integration.Id });
        }

        // ══════════════════════════════════════════════════════════════
        // EVENTS — Events Management API
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        [Route("LinkedIn/Events")]
        public async Task<IActionResult> Events(int? id)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var integration = await ResolveIntegrationAsync(id, userId.ToString());
            ViewBag.Integration = integration;
            ViewBag.AllIntegrations = await _db.LinkedInIntegrations.Where(l => l.UserId == userId.ToString() && l.IsActive).ToListAsync();

            List<(string Urn, string Name, string? LogoUrl)> organizations = new();
            if (integration != null)
            {
                var token = await _EnsureFreshTokenAsync(integration);
                if (!string.IsNullOrEmpty(token))
                {
                    try { organizations = await _linkedin.GetOrganizationsAsync(token); }
                    catch (Exception ex) { TempData["LinkedInError"] = "Could not load organization pages: " + ex.Message; }
                }
            }
            ViewBag.Organizations = organizations;

            var events = await _db.LinkedInEvents
                .Where(e => e.UserId == userId.ToString())
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();

            ViewBag.Success = TempData["LinkedInSuccess"] as string;
            ViewBag.Error = TempData["LinkedInError"] as string;

            return View(events);
        }

        [HttpPost]
        [Route("LinkedIn/CreateEvent")]
        public async Task<IActionResult> CreateEvent(
            int? id, string organizerUrn, string name, string? description,
            string eventType, string startsAt, string? endsAt, string? externalUrl)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var integration = await ResolveIntegrationAsync(id, userId.ToString());
            if (integration == null) { TempData["LinkedInError"] = "Connect LinkedIn first."; return RedirectToAction("Events"); }
            if (string.IsNullOrWhiteSpace(organizerUrn) || string.IsNullOrWhiteSpace(name))
            {
                TempData["LinkedInError"] = "Organization and event name are required.";
                return RedirectToAction("Events");
            }

            var token = await _EnsureFreshTokenAsync(integration);
            if (string.IsNullOrEmpty(token)) { TempData["LinkedInError"] = "LinkedIn token expired — please reconnect."; return RedirectToAction("Events"); }

            var startsAtMs = DateTimeOffset.Parse(startsAt).ToUnixTimeMilliseconds();
            long? endsAtMs = !string.IsNullOrWhiteSpace(endsAt) ? DateTimeOffset.Parse(endsAt).ToUnixTimeMilliseconds() : null;

            var record = new LinkedInEvent
            {
                UserId = userId.ToString(),
                LinkedInIntegrationId = integration.Id,
                Name = name,
                Description = description,
                EventType = eventType,
                OrganizerUrn = organizerUrn,
                StartsAt = startsAtMs,
                EndsAt = endsAtMs,
                ExternalUrl = externalUrl,
                Status = "draft"
            };

            try
            {
                var (eventId, liveVideoUrn, vanityName) = await _linkedin.CreateEventAsync(
                    token, organizerUrn, name, description, eventType, startsAtMs, endsAtMs, externalUrl);

                record.LinkedInEventId = eventId;
                record.LiveVideoUrn = liveVideoUrn;
                record.VanityName = vanityName;

                var contentRef = eventType == "online_livevideo" ? liveVideoUrn! : $"urn:li:event:{eventId}";
                var postUrn = await _linkedin.PostEventAsync(token, organizerUrn, contentRef);

                record.UgcPostUrn = postUrn;
                record.Status = "posted";
                TempData["LinkedInSuccess"] = $"Event \"{name}\" created and posted.";
            }
            catch (Exception ex)
            {
                record.Status = "failed";
                record.ErrorMessage = ex.Message;
                TempData["LinkedInError"] = "Event creation failed: " + ex.Message;
            }

            _db.LinkedInEvents.Add(record);
            await _db.SaveChangesAsync();

            return RedirectToAction("Events", new { id = integration.Id });
        }

        // ══════════════════════════════════════════════════════════════
        // CONVERSIONS — Conversions API
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        [Route("LinkedIn/Conversions")]
        public async Task<IActionResult> Conversions(int? id)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var integration = await ResolveIntegrationAsync(id, userId.ToString());
            ViewBag.Integration = integration;
            ViewBag.AllIntegrations = await _db.LinkedInIntegrations.Where(l => l.UserId == userId.ToString() && l.IsActive).ToListAsync();

            var rules = await _db.LinkedInConversionRules
                .Where(r => r.UserId == userId.ToString())
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();

            var ruleIds = rules.Select(r => r.Id).ToList();
            var recentEvents = await _db.LinkedInConversionEvents
                .Where(e => ruleIds.Contains(e.ConversionRuleId))
                .OrderByDescending(e => e.SentAt)
                .Take(50)
                .ToListAsync();

            ViewBag.RecentEvents = recentEvents;
            ViewBag.Success = TempData["LinkedInSuccess"] as string;
            ViewBag.Error = TempData["LinkedInError"] as string;

            return View(rules);
        }

        [HttpPost]
        [Route("LinkedIn/CreateConversionRule")]
        public async Task<IActionResult> CreateConversionRule(int? id, string name, string adAccountUrn, string conversionType)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var integration = await ResolveIntegrationAsync(id, userId.ToString());
            if (integration == null) { TempData["LinkedInError"] = "Connect LinkedIn first."; return RedirectToAction("Conversions"); }
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(adAccountUrn))
            {
                TempData["LinkedInError"] = "Name and ad account URN are required (e.g. urn:li:sponsoredAccount:12345).";
                return RedirectToAction("Conversions");
            }

            var token = await _EnsureFreshTokenAsync(integration);
            if (string.IsNullOrEmpty(token)) { TempData["LinkedInError"] = "LinkedIn token expired — please reconnect."; return RedirectToAction("Conversions"); }

            var rule = new LinkedInConversionRule
            {
                UserId = userId.ToString(),
                LinkedInIntegrationId = integration.Id,
                Name = name,
                AdAccountUrn = adAccountUrn,
                ConversionType = conversionType
            };

            try
            {
                var urn = await _linkedin.CreateConversionRuleAsync(token, name, adAccountUrn, conversionType);
                rule.ConversionUrn = urn;
                rule.Status = "created";
                TempData["LinkedInSuccess"] = $"Conversion rule \"{name}\" created.";
            }
            catch (Exception ex)
            {
                rule.Status = "failed";
                rule.ErrorMessage = ex.Message;
                TempData["LinkedInError"] = "Conversion rule creation failed: " + ex.Message + " (needs the connected account to have a role on this ad account)";
            }

            _db.LinkedInConversionRules.Add(rule);
            await _db.SaveChangesAsync();

            return RedirectToAction("Conversions", new { id = integration.Id });
        }

        [HttpPost]
        [Route("LinkedIn/SendConversionEvent")]
        public async Task<IActionResult> SendConversionEvent(int ruleId, int? id, decimal? amount, string? currencyCode, string userIdentifierType, string userIdentifierValue)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var rule = await _db.LinkedInConversionRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.UserId == userId.ToString());
            if (rule == null || string.IsNullOrEmpty(rule.ConversionUrn))
            {
                TempData["LinkedInError"] = "Conversion rule not found or not yet created on LinkedIn.";
                return RedirectToAction("Conversions");
            }

            var integration = await ResolveIntegrationAsync(id ?? rule.LinkedInIntegrationId, userId.ToString());
            if (integration == null) { TempData["LinkedInError"] = "Connect LinkedIn first."; return RedirectToAction("Conversions"); }

            var token = await _EnsureFreshTokenAsync(integration);
            if (string.IsNullOrEmpty(token)) { TempData["LinkedInError"] = "LinkedIn token expired — please reconnect."; return RedirectToAction("Conversions"); }

            var eventId = Guid.NewGuid().ToString();
            var evt = new LinkedInConversionEvent
            {
                ConversionRuleId = rule.Id,
                EventId = eventId,
                Amount = amount,
                CurrencyCode = currencyCode,
                UserIdentifierType = userIdentifierType
            };

            try
            {
                await _linkedin.StreamConversionEventAsync(
                    token, rule.ConversionUrn, eventId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    amount, currencyCode, userIdentifierType, userIdentifierValue);
                evt.Success = true;
                TempData["LinkedInSuccess"] = "Conversion event sent.";
            }
            catch (Exception ex)
            {
                evt.Success = false;
                evt.ErrorMessage = ex.Message;
                TempData["LinkedInError"] = "Conversion event failed: " + ex.Message;
            }

            _db.LinkedInConversionEvents.Add(evt);
            await _db.SaveChangesAsync();

            return RedirectToAction("Conversions", new { id = integration.Id });
        }

        // ══ Private helpers ════════════════════════════════════════════

        // The state param is a Data-Protection-signed token (userId + issue time),
        // not a session-stored nonce — it self-verifies via Unprotect, so it
        // survives the LinkedIn redirect round-trip even if the session cookie
        // doesn't (Connect() never wrote it to session, so comparing against
        // session here always failed with "OAuth session expired").
        private bool _ValidateState(string? state, out string error)
        {
            error = "";

            if (string.IsNullOrWhiteSpace(state))
            {
                error = "Missing state.";
                return false;
            }

            string raw;
            try
            {
                raw = _protector.Unprotect(state);
            }
            catch
            {
                error = "Invalid or expired state.";
                return false;
            }

            var parts = raw.Split('|');
            if (parts.Length != 3 || parts[0] != "linkedin")
            {
                error = "Invalid state.";
                return false;
            }

            if (!long.TryParse(parts[2], out var issuedAt) ||
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issuedAt > 600)
            {
                error = "OAuth state expired — please try connecting again.";
                return false;
            }

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