//using Google.Apis.Auth.OAuth2;
//using Google.Apis.Auth.OAuth2.Flows;
//using Google.Apis.Util.Store;
//using Microsoft.AspNetCore.DataProtection;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.EntityFrameworkCore;
//using SocialMediaPanel.Data;
//using SocialMediaPanel.Models;
//using SocialMediaPanel.Services;

//namespace SocialMediaPanel.Controllers
//{
//    public class IntegrationsController : Controller
//    {
//        private readonly FacebookService _facebook;
//        private readonly GmailService _gmail;
//        private readonly IDataProtector _protector;
//        private readonly AppDbContext _context;
//        private readonly InstagramService _instagram;

//        public IntegrationsController(
//             FacebookService facebook,
//             GmailService gmail,
//             AppDbContext context,
//             InstagramService instagramService,
//             IDataProtectionProvider dataProtection)
//        {
//            _context = context;
//            _gmail = gmail;
//            _facebook = facebook;
//            _instagram = instagramService;
//            _protector = dataProtection.CreateProtector("Integrations.Facebook.OAuthState");
//        }


//        [HttpGet]
//        [Route("Integrations/Modal/{platform}")]
//        public IActionResult Modal(string platform)
//        {
//            switch (platform.ToLower())
//            {
//                case "facebook": return _FacebookModal();
//                case "gmail": return _GmailModal();
//                case "instagram": return _InstagramModal();
//                default: return NotFound($"No modal for: {platform}");
//            }
//        }

//        private IActionResult _InstagramModal()
//        {
//            string oauthUrl = "#";
//            string? oauthErr = null;

//            try
//            {
//                //var state = Guid.NewGuid().ToString("N");
//                //try { HttpContext.Session.SetString("fb_oauth_state", state); } catch { }
//                var userId = User.Identity?.Name ?? "anonymous";
//                var raw = $"{userId}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
//                var state = _protector.Protect(raw);

//                oauthUrl = _instagram.BuildOAuthUrl(state);
//            }
//            catch
//            {
//                oauthErr = "Facebook App ID / Secret not configured in appsettings.json";
//            }

//            ViewData["OAuthUrl"] = oauthUrl;
//            ViewData["OAuthError"] = oauthErr;
//            ViewData["IsReconnecting"] = false;
//            ViewData["ConnectedPageName"] = null;

//            return PartialView("~/Views/Shared/Integrations/_Instagram.cshtml");
//        }

//        [HttpGet]
//        [Route("Integrations/Callback/instagram")]
//        public async Task<IActionResult> InstagramCallback(
//     string? code,
//     string? state,
//     string? error,
//     string? error_description)
//        {
//            // ── Handle OAuth errors ─────────────────────────────
//            if (error != null)
//            {
//                TempData["IntegrationError"] = $"Instagram access denied: {error_description}";
//                return RedirectToAction("Index", "Dashboard");
//            }

//            // ── Validate state ──────────────────────────────────
//            if (string.IsNullOrEmpty(state))
//            {
//                TempData["IntegrationError"] = "Missing state parameter. Please try again.";
//                return RedirectToAction("Index", "Dashboard");
//            }

//            try
//            {
//                var raw = _protector.Unprotect(state);
//                var parts = raw.Split('|');

//                if (parts.Length == 2 && long.TryParse(parts[1], out var ts))
//                {
//                    var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts;
//                    if (age > 600)
//                    {
//                        TempData["IntegrationError"] = "OAuth session expired. Please try again.";
//                        return RedirectToAction("Index", "Dashboard");
//                    }
//                }
//            }
//            catch
//            {
//                TempData["IntegrationError"] = "Invalid state parameter. Please try again.";
//                return RedirectToAction("Index", "Dashboard");
//            }

//            // ── Check code ──────────────────────────────────────
//            if (string.IsNullOrEmpty(code))
//            {
//                TempData["IntegrationError"] = "No authorisation code received from Instagram.";
//                return RedirectToAction("Index", "Dashboard");
//            }

//            try
//            {
//                // Step 1: Exchange token
//                //var userToken = await _instagram.ExchangeCodeAsync(code);
//                var shortToken = await _instagram.ExchangeCodeAsync(code);

//                var longToken = await _instagram.GetLongLivedTokenAsync(shortToken);

//                var account = await _instagram.GetInstagramAccountsAsync(longToken);



//                //// Step 2: Get Instagram accounts via pages
//                //var accounts = await _instagram.GetInstagramAccountsAsync(userToken);

//                //if (accounts.Count == 0)
//                //{
//                //    TempData["IntegrationError"] = "No Instagram Business account found. Please connect your Instagram to a Facebook Page.";
//                //    return RedirectToAction("Index", "Dashboard");
//                //}

//                //var account = accounts.First();

//                var Userid = HttpContext.Session.GetInt32("UserId");
//                var username = HttpContext.Session.GetString("UserEmail");

//                // Step 3: Save token
//                var token = new UserToken
//                {
//                    userId = (int)Userid,
//                    username = username.ToString(),
//                    instagramtoken = longToken,
//                    CreatedAt = DateTime.UtcNow
//                };

//                _context.UserTokens.Add(token);
//                var res = await _context.SaveChangesAsync();

//                var existingAccount = await _context.InstagramAccounts
//    .FirstOrDefaultAsync(x => x.InstagramUserId == account.InstagramId
//                              && x.UserId == Userid.ToString());


//                    var igAccount = new InstagramAccount
//                    {
//                        UserId = Userid.ToString(),
//                        Username = username.ToString(),
//                        InstagramUserId = account.InstagramId,
//                        Name = account.Username,
//                        ProfilePictureUrl = account.ProfilePicture,
//                        CreatedAt = DateTime.UtcNow
//                    };

//                    _context.InstagramAccounts.Add(igAccount);

//                var resig = await _context.SaveChangesAsync();


//                TempData["IntegrationSuccess"] = "instagram";
//                TempData["InstagramName"] = account.Username;
//            }
//            catch (Exception ex)
//            {
//                TempData["IntegrationError"] = "Instagram connection failed: " + ex.Message;
//            }

//            return RedirectToAction("Index", "Dashboard");
//        }


//        private IActionResult _GmailModal()
//        {
//            var state = Guid.NewGuid().ToString();  // important for security
//            HttpContext.Session.SetString("gmail_oauth_state", state);

//            var url = _gmail.BuildOAuthUrl(state);

//            ViewData["OAuthUrl"] = url;
//            ViewData["OAuthError"] = null;
//            ViewData["IsReconnecting"] = false;
//            ViewData["ConnectedPageName"] = null;

//            return PartialView("~/Views/Shared/Integrations/_Gmail.cshtml");
//        }

//        public async Task<IActionResult> GmailCallback(string code, string state)
//        {
//            // 1. Validate state (VERY IMPORTANT)
//            var savedState = HttpContext.Session.GetString("gmail_oauth_state");

//            if (state != savedState)
//            {
//                return BadRequest("Invalid state");
//            }

//            // 2. Exchange code → token
//            var token = await _gmail.ExchangeCodeAsync(code);

//            // 3. Get user profile (email, name)
//            var profile = await _gmail.GetProfileAsync(token.AccessToken);

//            var userid = HttpContext.Session.GetInt32("UserId");

//            // 4. Save in DB (your gmail_integrations table)
//            var data = new GmailIntegration
//            {
//                UserId = userid.ToString(), // from login system
//                GoogleAccountId = profile.GoogleId,
//                EmailAddress = profile.Email,
//                DisplayName = profile.DisplayName,
//                ProfilePicture = profile.PictureUrl,

//                AccessToken = token.AccessToken,
//                RefreshToken = token.RefreshToken,
//                TokenExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn),

//                GrantedScopes = token.Scope,
//                IsActive = true,
//                ConnectedAt = DateTime.UtcNow
//            };

//            _context.GmailIntegrations.Add(data);
//            await _context.SaveChangesAsync();

//            return RedirectToAction("Index", "Dashboard");
//        }


//        private IActionResult _FacebookModal()
//        {
//            string oauthUrl = "#";
//            string? oauthErr = null;

//            try
//            {
//                //var state = Guid.NewGuid().ToString("N");
//                //try { HttpContext.Session.SetString("fb_oauth_state", state); } catch { }
//                var userId = User.Identity?.Name ?? "anonymous";
//                var raw = $"{userId}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
//                var state = _protector.Protect(raw);

//                oauthUrl = _facebook.BuildOAuthUrl(state);
//            }
//            catch
//            {
//                oauthErr = "Facebook App ID / Secret not configured in appsettings.json";
//            }

//            ViewData["OAuthUrl"] = oauthUrl;
//            ViewData["OAuthError"] = oauthErr;
//            ViewData["IsReconnecting"] = false;
//            ViewData["ConnectedPageName"] = null;

//            return PartialView("~/Views/Shared/Integrations/_Facebook.cshtml");
//        }

//        [HttpGet]
//        [Route("Integrations/Callback/facebook")]
//        public async Task<IActionResult> FacebookCallback(
//           string? code,
//           string? state,
//           string? error,
//           string? error_description)
//        {
//            if (error != null)
//            {
//                TempData["IntegrationError"] = $"Facebook access denied: {error_description}";
//                return RedirectToAction("Index", "Dashboard");
//            }

//            if (string.IsNullOrEmpty(state))
//            {
//                TempData["IntegrationError"] = "Missing state parameter. Please try again.";
//                return RedirectToAction("Index", "Dashboard");
//            }

//            try
//            {
//                var raw = _protector.Unprotect(state);  // throws if tampered
//                var parts = raw.Split('|');

//                // Validate timestamp — reject tokens older than 10 minutes
//                if (parts.Length == 2 &&
//                    long.TryParse(parts[1], out var ts))
//                {
//                    var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts;
//                    if (age > 600) 
//                    {
//                        TempData["IntegrationError"] = "OAuth session expired. Please try again.";
//                        return RedirectToAction("Index", "Dashboard");
//                    }
//                }
//            }
//            catch
//            {
//                TempData["IntegrationError"] = "Invalid state parameter. Please try again.";
//                return RedirectToAction("Index", "Dashboard");
//            }

//            if (string.IsNullOrEmpty(code))
//            {
//                TempData["IntegrationError"] = "No authorisation code received from Facebook.";
//                return RedirectToAction("Index", "Dashboard");
//            }

//            try
//            {
//                var tokenResult = await _facebook.ExchangeCodeAsync(code);
//                var pages = await _facebook.GetManagedPagesAsync(tokenResult.LongLivedToken);

//                var userid = HttpContext.Session.GetInt32("UserId");
//                var username = HttpContext.Session.GetInt32("UserEmail");


//                var token = new UserToken
//                {
//                    userId = (int)userid,
//                    username = username.ToString(),
//                    facebooktoken = tokenResult.LongLivedToken,
//                    CreatedAt = DateTime.UtcNow
//                };

//                _context.UserTokens.Add(token);

//                foreach (var p in pages)
//                {
//                    var page = new FacebookPageEntity
//                    {
//                        user_id = userid.ToString(),
//                        user_name = username.ToString(), 
//                        page_id = p.PageId,
//                        page_name = p.Name,
//                        page_access_token = p.AccessToken,
//                        created_at = DateTime.UtcNow
//                    };

//                    _context.FacebookPages.Add(page);
//                }

//                await _context.SaveChangesAsync();
//                var res = await _context.SaveChangesAsync();
//                TempData["IntegrationSuccess"] = "facebook";
//                TempData["FacebookPageName"] = pages.FirstOrDefault()?.Name ?? "your page";
//            }
//            catch (Exception ex)
//            {
//                TempData["IntegrationError"] = "Connection failed: " + ex.Message;
//            }

//            return RedirectToAction("Index", "Dashboard");
//        }

//        [HttpPost]
//        [Route("Integrations/Disconnect/{platform}")]
//        [ValidateAntiForgeryToken]
//        public IActionResult Disconnect(string platform)
//        {
//            return Json(new { success = true });
//        }
//    }

//    public class FacebookPageEntity
//    {
//        public int id { get; set; }
//        public string? user_id { get; set; }
//        public string user_name { get; set; }
//        public string page_id { get; set; }
//        public string page_name { get; set; }
//        public string page_access_token { get; set; }
//        public DateTime created_at { get; set; }
//    }

//    public class InstagramAccount
//    {
//        public int Id { get; set; }

//        public string UserId { get; set; } // your app user

//        public string InstagramUserId { get; set; }
//        public string Username { get; set; }
//        public string Name { get; set; }
//        public string ProfilePictureUrl { get; set; }

//        public DateTime CreatedAt { get; set; } = DateTime.Now;
//    }
//}








using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Util.Store;
using LinqToTwitter;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using SocialMediaPanel.Services;

namespace SocialMediaPanel.Controllers
{
    public class IntegrationsController : Controller
    {
        private readonly FacebookService _facebook;
        private readonly GmailService _gmail;
        private readonly IDataProtector _protector;
        private readonly AppDbContext _context;
        private readonly InstagramService _instagram;
        private readonly NativeInstagramService _nativeInstagram;
        private readonly LinkedInService _linkedInService;
        private readonly ActivePageService _activePages;
        private readonly GmailIntegrationService _gmailIntegration;

        private readonly ILogger<IntegrationsController> _logger;

        public IntegrationsController(
            FacebookService facebook,
            GmailService gmail,
            AppDbContext context,
            InstagramService instagramService,
            NativeInstagramService nativeInstagramService,
            IDataProtectionProvider dataProtection,
            LinkedInService linkedInService,
            ActivePageService activePages,
            GmailIntegrationService gmailIntegration,
            ILogger<IntegrationsController> logger)
        {
            _context = context;
            _gmail = gmail;
            _facebook = facebook;
            _instagram = instagramService;
            _nativeInstagram = nativeInstagramService;
            _protector = dataProtection.CreateProtector("Integrations.Facebook.OAuthState");
            _linkedInService = linkedInService;
            _activePages = activePages;
            _gmailIntegration = gmailIntegration;
            _logger = logger;
        }

        // ══════════════════════════════════════════════════════════════
        // PAGE SWITCHER — list connected pages/accounts + set the active one
        // Used by _PageSwitcher.cshtml
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        [Route("Integrations/MyPages")]
        public async Task<IActionResult> MyPages()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return Json(new { success = false, message = "Not logged in" });

            var fbPages = await _activePages.GetAllFacebookPagesAsync(userId.Value);
            var igAccounts = await _activePages.GetAllInstagramAccountsAsync(userId.Value);
            var activeFb = await _activePages.GetActiveFacebookPageAsync(userId.Value);
            var activeIg = await _activePages.GetActiveInstagramAccountAsync(userId.Value);

            return Json(new
            {
                success = true,
                facebookPages = fbPages.Select(p => new { p.page_id, p.page_name }),
                instagramAccounts = igAccounts.Select(a => new { id = a.InstagramUserId, name = a.Username }),
                activeFacebookPageId = activeFb?.page_id,
                activeInstagramId = activeIg?.InstagramUserId
            });
        }

        // ══════════════════════════════════════════════════════════════
        // NATIVE INSTAGRAM LOGIN — direct connect, no Facebook Page needed
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        [Route("Integrations/ConnectInstagramNative")]
        public IActionResult ConnectInstagramNative()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var raw = $"ignative|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var state = _protector.Protect(raw);
            var url = _nativeInstagram.BuildOAuthUrl(state);
            return Redirect(url);
        }

        [HttpGet]
        [Route("Integrations/Callback/instagram-native")]
        public async Task<IActionResult> InstagramNativeCallback(string? code, string? error, string? error_description)
        {
            if (error != null)
            {
                TempData["IntegrationError"] = $"Instagram access denied: {error_description}";
                return RedirectToAction("Index", "Dashboard");
            }

            if (string.IsNullOrEmpty(code))
            {
                TempData["IntegrationError"] = "No authorisation code received from Instagram.";
                return RedirectToAction("Index", "Dashboard");
            }

            try
            {
                var userid = HttpContext.Session.GetInt32("UserId");
                if (userid == null)
                {
                    TempData["IntegrationError"] = "Session expired. Please login again.";
                    return RedirectToAction("Login", "Account");
                }

                var (shortToken, igUserId) = await _nativeInstagram.ExchangeCodeAsync(code);
                var longToken = await _nativeInstagram.GetLongLivedTokenAsync(shortToken);
                var profile = await _nativeInstagram.GetProfileAsync(longToken);

                var existing = await _context.InstagramAccounts
                    .FirstOrDefaultAsync(a => a.UserId == userid.ToString() && a.InstagramUserId == igUserId);

                if (existing != null)
                {
                    existing.Username = profile.Username;
                    existing.Name = profile.Username;
                    existing.ProfilePictureUrl = profile.ProfilePictureUrl;
                    existing.NativeAccessToken = longToken;
                }
                else
                {
                    _context.InstagramAccounts.Add(new InstagramAccount
                    {
                        UserId = userid.ToString(),
                        InstagramUserId = igUserId,
                        Username = profile.Username,
                        Name = profile.Username,
                        ProfilePictureUrl = profile.ProfilePictureUrl,
                        NativeAccessToken = longToken,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                await _context.SaveChangesAsync();

                // IntegrationSuccess must stay the bare platform keyword — Dashboard's
                // success script passes it straight into PlatformAuth.openSuccess(),
                // which uses it both as a BANNERS[] lookup key and as the path segment
                // for GET /Integrations/Modal/{platform}. Putting the descriptive
                // sentence here instead (as this used to) turned that into
                // /Integrations/Modal/Instagram account @user connected directly...
                // — a 404, since routing takes it as a literal platform name. The
                // human-readable text belongs in FacebookPageName, same as every other
                // platform's success message.
                TempData["IntegrationSuccess"] = "instagram";
                TempData["FacebookPageName"] = $"@{profile.Username} (connected directly)";
            }
            catch (Exception ex)
            {
                TempData["IntegrationError"] = $"Instagram native connect failed: {ex.Message}";
            }

            return RedirectToAction("Index", "Dashboard");
        }

        public async Task<IActionResult> ConnectedAccounts()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var fbPages = await _activePages.GetAllFacebookPagesAsync(userId.Value);
            var igAccounts = await _activePages.GetAllInstagramAccountsAsync(userId.Value);
            var activeFb = await _activePages.GetActiveFacebookPageAsync(userId.Value);
            var activeIg = await _activePages.GetActiveInstagramAccountAsync(userId.Value);

            ViewBag.ActiveFbId = activeFb?.page_id;
            ViewBag.ActiveIgId = activeIg?.InstagramUserId;
            ViewBag.FbPages = fbPages;
            ViewBag.IgAccounts = igAccounts;

            return View();
        }

        [HttpPost]
        [Route("Integrations/SetActivePage")]
        public async Task<IActionResult> SetActivePage([FromForm] string? platform, [FromForm] string? pageId)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return Json(new { success = false, message = "Not logged in" });
            if (string.IsNullOrEmpty(pageId)) return Json(new { success = false, message = "pageId required" });

            if (platform?.ToLower() == "instagram")
            {
                var ig = await _activePages.GetActiveInstagramAccountAsync(userId.Value, pageId);
                if (ig == null) return Json(new { success = false, message = "Instagram account not found for this user." });
            }
            else
            {
                var page = await _activePages.GetActiveFacebookPageAsync(userId.Value, pageId);
                if (page == null) return Json(new { success = false, message = "Page not found for this user." });
            }

            return Json(new { success = true });
        }

        // ── Modal router ──────────────────────────────────────────────
        [HttpGet]
        [Route("Integrations/Modal/{platform}")]
        public IActionResult Modal(string platform)
        {
            switch (platform.ToLower())
            {
                case "facebook": return _FacebookModal();
                case "instagram": return _InstagramModal(); // Instagram Facebook se connect hoga
                case "gmail": return _GmailModal();
                case "linkedin": return _LinkedInModal();
                default: return NotFound($"No modal for: {platform}");
            }
        }

        private IActionResult _LinkedInModal()
        {
            string oauthUrl = "#";
            string? oauthErr = null;

            try
            {
                var userId = HttpContext.Session.GetInt32("UserId");

                if (userId == null)
                    throw new Exception("User session expired.");

                // Create random state
                var state = Guid.NewGuid().ToString("N");

                // Save in Session
                HttpContext.Session.SetString("LinkedInOAuthState", state);
                HttpContext.Session.SetString("LinkedInOAuthUserId", userId.ToString());

                oauthUrl = _linkedInService.BuildOAuthUrl(state);
            }
            catch (Exception ex)
            {
                oauthErr = ex.Message;
            }

            ViewData["OAuthUrl"] = oauthUrl;
            ViewData["OAuthError"] = oauthErr;

            return PartialView("~/Views/Shared/Integrations/_LinkedIn.cshtml");
        }

        // ── Facebook Modal ────────────────────────────────────────────
        private IActionResult _FacebookModal()
        {
            string oauthUrl = "#";
            string? oauthErr = null;

            try
            {
                var raw = $"fb|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                var state = _protector.Protect(raw);
                oauthUrl = _facebook.BuildOAuthUrl(state);
            }
            catch
            {
                oauthErr = "Facebook App ID / Secret not configured in appsettings.json";
            }

            ViewData["OAuthUrl"] = oauthUrl;
            ViewData["OAuthError"] = oauthErr;
            return PartialView("~/Views/Shared/Integrations/_Facebook.cshtml");
        }

        // ── Instagram Modal ───────────────────────────────────────────
        private IActionResult _InstagramModal()
        {
            string oauthUrl = "#";
            string? oauthErr = null;

            try
            {
                // Instagram also uses Facebook OAuth but with instagram_basic scope
                // State prefix "ig" so callback knows which flow this is
                var raw = $"ig|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                var state = _protector.Protect(raw);
                oauthUrl = _instagram.BuildOAuthUrl(state); // same FB OAuth URL
            }
            catch
            {
                oauthErr = "Instagram / Facebook App not configured in appsettings.json";
            }

            ViewData["OAuthUrl"] = oauthUrl;
            ViewData["OAuthError"] = oauthErr;
            return PartialView("~/Views/Shared/Integrations/_Instagram.cshtml");
        }

        // ── Facebook Callback ─────────────────────────────────────────
        [HttpGet]
        [Route("Integrations/Callback/facebook")]
        public async Task<IActionResult> FacebookCallback(
            string? code,
            string? state,
            string? error,
            string? error_description)
        {
            if (error != null)
            {
                TempData["IntegrationError"] = $"Facebook access denied: {error_description}";
                return RedirectToAction("Index", "Dashboard");
            }

            if (string.IsNullOrEmpty(code))
            {
                TempData["IntegrationError"] = "No authorisation code received from Facebook.";
                return RedirectToAction("Index", "Dashboard");
            }

            try
            {
                var userid = HttpContext.Session.GetInt32("UserId");
                var username = HttpContext.Session.GetString("UserEmail") ?? "";

                if (userid == null)
                {
                    TempData["IntegrationError"] = "Session expired. Please login again.";
                    return RedirectToAction("Login", "Account");
                }

                // Token exchange
                var tokenResult = await _facebook.ExchangeCodeAsync(code);
                var pages = await _facebook.GetManagedPagesAsync(tokenResult.LongLivedToken);

                // Save user token
                _context.UserTokens.Add(new UserToken
                {
                    userId = (int)userid,
                    username = username,
                    facebooktoken = tokenResult.LongLivedToken,
                    CreatedAt = DateTime.UtcNow
                });

                // Save Facebook pages only
                foreach (var p in pages)
                {
                    var existingPage = await _context.FacebookPages
                        .FirstOrDefaultAsync(x => x.user_id == userid.ToString() && x.page_id == p.PageId);

                    if (existingPage != null)
                    {
                        existingPage.page_access_token = p.AccessToken;
                        existingPage.page_name = p.Name;
                    }
                    else
                    {
                        _context.FacebookPages.Add(new FacebookPageEntity
                        {
                            user_id = userid.ToString(),
                            user_name = username,
                            page_id = p.PageId,
                            page_name = p.Name,
                            page_access_token = p.AccessToken,
                            created_at = DateTime.UtcNow
                        });
                    }
                }

                await _context.SaveChangesAsync();

                TempData["IntegrationSuccess"] = "facebook";
                TempData["FacebookPageName"] = pages.FirstOrDefault()?.Name ?? "your page";
            }
            catch (Exception ex)
            {
                TempData["IntegrationError"] = "Facebook connection failed: " + ex.Message;
            }

            return RedirectToAction("Index", "Dashboard");
        }

        // ── Instagram Callback ────────────────────────────────────────
        [HttpGet]
        [Route("Integrations/Callback/instagram")]
        public async Task<IActionResult> InstagramCallback(
     string? code,
     string? state,
     string? error,
     string? error_description)
        {
            if (error != null)
            {
                TempData["IntegrationError"] = $"Instagram access denied: {error_description}";
                return RedirectToAction("Index", "Dashboard");
            }

            if (string.IsNullOrEmpty(code))
            {
                TempData["IntegrationError"] = "No authorisation code received.";
                return RedirectToAction("Index", "Dashboard");
            }

            try
            {
                var userid = HttpContext.Session.GetInt32("UserId");
                var username = HttpContext.Session.GetString("UserEmail") ?? "";

                if (userid == null)
                {
                    TempData["IntegrationError"] = "Session expired. Please login again.";
                    return RedirectToAction("Login", "Account");
                }

                // ✅ Use _instagram (has correct redirect URI) not _facebook
                var shortToken = await _instagram.ExchangeCodeAsync(code);

                // ✅ Get long-lived token via Facebook
                var longToken = await _instagram.GetLongLivedTokenAsync(shortToken);

                // ✅ Get pages using long-lived token
                var pages = await _facebook.GetManagedPagesAsync(longToken);

                if (pages == null || !pages.Any())
                {
                    TempData["IntegrationError"] = "No Facebook Pages found. You need a Facebook Page linked to your Instagram Business Account.";
                    return RedirectToAction("Index", "Dashboard");
                }

                // Save Instagram token
                var existingToken = await _context.UserTokens
                    .FirstOrDefaultAsync(t => t.userId == userid);

                if (existingToken != null)
                    existingToken.instagramtoken = longToken;
                else
                    _context.UserTokens.Add(new UserToken
                    {
                        userId = (int)userid,
                        username = username,
                        instagramtoken = longToken,
                        CreatedAt = DateTime.UtcNow
                    });

                // Find linked Instagram Business Account for each page
                var client = new HttpClient();
                int igCount = 0;

                foreach (var p in pages)
                {
                    try
                    {
                        var url = $"https://graph.facebook.com/v19.0/{p.PageId}" +
                                  $"?fields=instagram_business_account{{id,username,name,profile_picture_url}}" +
                                  $"&access_token={p.AccessToken}";

                        var res = await client.GetStringAsync(url);
                        var json = System.Text.Json.JsonDocument.Parse(res);

                        if (!json.RootElement.TryGetProperty("instagram_business_account", out var ig))
                        {
                            Console.WriteLine($"No IG linked to page: {p.Name}");
                            continue;
                        }

                        var igId = ig.TryGetProperty("id", out var igid) ? igid.GetString() : null;
                        // Fall back to the IG id itself (never the FB login email) if Instagram's
                        // Graph API response is missing username/name — this is a different
                        // identity from the connecting user and must never be confused with it.
                        var igUsr = ig.TryGetProperty("username", out var igun) && !string.IsNullOrEmpty(igun.GetString()) ? igun.GetString() : igId;
                        var igNm = ig.TryGetProperty("name", out var ign) && !string.IsNullOrEmpty(ign.GetString()) ? ign.GetString() : igId;
                        var igPic = ig.TryGetProperty("profile_picture_url", out var igpic) ? igpic.GetString() : "";

                        if (string.IsNullOrEmpty(igId)) continue;

                        var existing = await _context.InstagramAccounts
                            .FirstOrDefaultAsync(a => a.UserId == userid.ToString()
                                                   && a.InstagramUserId == igId);
                        if (existing != null)
                        {
                            existing.Username = igUsr ?? existing.Username;
                            existing.Name = igNm ?? existing.Name;
                            existing.ProfilePictureUrl = igPic ?? existing.ProfilePictureUrl;
                        }
                        else
                        {
                            _context.InstagramAccounts.Add(new InstagramAccount
                            {
                                UserId = userid.ToString(),
                                InstagramUserId = igId,
                                Username = igUsr ?? "",
                                Name = igNm ?? "",
                                ProfilePictureUrl = igPic ?? "",
                                CreatedAt = DateTime.UtcNow
                            });
                        }

                        igCount++;
                        Console.WriteLine($"Instagram saved: @{igUsr} ({igId})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Page {p.Name} failed: {ex.Message}");
                    }
                }

                await _context.SaveChangesAsync();

                TempData[igCount == 0 ? "IntegrationError" : "IntegrationSuccess"] = igCount == 0
                    ? "No Instagram Business Account linked to your Facebook Page. Go to Facebook Page Settings → Instagram → Connect Account first."
                    : "instagram";
            }
            catch (Exception ex)
            {
                TempData["IntegrationError"] = "Instagram connection failed: " + ex.Message;
            }

            return RedirectToAction("Index", "Dashboard");
        }

        // ── Gmail Modal ───────────────────────────────────────────────
        private IActionResult _GmailModal()
        {
            var state = Guid.NewGuid().ToString();
            HttpContext.Session.SetString("gmail_oauth_state", state);

            var url = _gmail.BuildOAuthUrl(state);

            ViewData["OAuthUrl"] = url;
            ViewData["OAuthError"] = null;
            ViewData["IsReconnecting"] = false;
            ViewData["ConnectedPageName"] = null;

            return PartialView("~/Views/Shared/Integrations/_Gmail.cshtml");
        }

        // ── Gmail Callback ────────────────────────────────────────────
        public async Task<IActionResult> GmailCallback(string code, string state)
        {
            var savedState = HttpContext.Session.GetString("gmail_oauth_state");
            if (state != savedState)
                return BadRequest("Invalid state");

            // SECURITY: without this check, an expired/missing session meant
            // userid.ToString() silently wrote UserId="" — GmailIntegrationService
            // .GetAsync("") would then match that row for ANY unauthenticated
            // caller (this is exactly how a real leak happened — see
            // feedback-test-against-live-prod-carefully memory).
            var userid = HttpContext.Session.GetInt32("UserId");
            if (userid == null)
            {
                TempData["IntegrationError"] = "Session expired. Please login again.";
                return RedirectToAction("Login", "Account");
            }

            try
            {
                var token = await _gmail.ExchangeCodeAsync(code);
                var profile = await _gmail.GetProfileAsync(token.AccessToken);

                // Upsert via GmailIntegrationService instead of a raw Add() — the
                // raw insert created a fresh duplicate row on every reconnect.
                await _gmailIntegration.SaveAsync(userid.Value.ToString(), token, profile);

                TempData["IntegrationSuccess"] = "gmail";
                TempData["FacebookPageName"] = profile.Email;
            }
            catch (Exception ex)
            {
                // Google's authorization `code` is single-use. A duplicate callback
                // request — browser prefetch, a double-click on the consent screen,
                // back/forward-cache replay — resends the same `code`, and the
                // SECOND attempt gets a 400 (invalid_grant) from Google even though
                // the connection already succeeded on the first attempt. Before
                // reporting failure, check whether this exact user already has a
                // Gmail integration that was (re)connected in roughly the last
                // minute — if so, this failed request was the redundant duplicate,
                // not a real failure, so surface success instead of a scary error.
                var recent = await _gmailIntegration.GetAsync(userid.Value.ToString());
                if (recent != null && recent.ConnectedAt >= DateTime.UtcNow.AddMinutes(-1))
                {
                    TempData["IntegrationSuccess"] = "gmail";
                    TempData["FacebookPageName"] = recent.EmailAddress;
                }
                else
                {
                    // Logged (not just shown in TempData) so the real Google
                    // error body — invalid_grant / redirect_uri_mismatch /
                    // invalid_client / etc. — is captured server-side even
                    // when nobody screenshots the on-screen banner.
                    _logger.LogError("Gmail connection failed for user {UserId}: {Msg}", userid, ex.Message);
                    TempData["IntegrationError"] = "Gmail connection failed: " + ex.Message;
                }
            }

            return RedirectToAction("Index", "Dashboard");
        }

        // ── Disconnect ────────────────────────────────────────────────
        // Was previously a stub that always returned success without touching
        // the DB — the account stayed connected. Actually removes the row now.
        // No [ValidateAntiForgeryToken] to match SetActivePage above (plain
        // fetch() call, no Razor form/antiforgery cookie backing it).
        [HttpPost]
        [Route("Integrations/Disconnect/{platform}")]
        public async Task<IActionResult> Disconnect(string platform, [FromForm] string? pageId)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return Json(new { success = false, message = "Not logged in" });
            if (string.IsNullOrEmpty(pageId)) return Json(new { success = false, message = "pageId required" });

            if (platform?.ToLower() == "instagram")
            {
                var acc = await _context.InstagramAccounts
                    .FirstOrDefaultAsync(a => a.UserId == userId.ToString() && a.InstagramUserId == pageId);
                if (acc == null) return Json(new { success = false, message = "Instagram account not found." });

                _context.InstagramAccounts.Remove(acc);
                await _context.SaveChangesAsync();

                if (HttpContext.Session.GetString("ActiveIgAccountId") == pageId)
                    HttpContext.Session.Remove("ActiveIgAccountId");
            }
            else if (platform?.ToLower() == "facebook")
            {
                var page = await _context.FacebookPages
                    .FirstOrDefaultAsync(p => p.user_id == userId.ToString() && p.page_id == pageId);
                if (page == null) return Json(new { success = false, message = "Facebook Page not found." });

                _context.FacebookPages.Remove(page);
                await _context.SaveChangesAsync();

                if (HttpContext.Session.GetString("ActiveFbPageId") == pageId)
                    HttpContext.Session.Remove("ActiveFbPageId");
            }
            else
            {
                return Json(new { success = false, message = "Unknown platform: " + platform });
            }

            return Json(new { success = true });
        }
    }

    // ── Models ────────────────────────────────────────────────────────
    public class FacebookPageEntity
    {
        public int id { get; set; }
        public string? user_id { get; set; }
        public string user_name { get; set; } = "";
        public string page_id { get; set; } = "";
        public string page_name { get; set; } = "";
        public string page_access_token { get; set; } = "";
        public DateTime created_at { get; set; }
    }

    public class InstagramAccount
    {
        public int Id { get; set; }
        public string UserId { get; set; } = "";
        public string InstagramUserId { get; set; } = "";
        public string Username { get; set; } = "";
        public string Name { get; set; } = "";
        public string ProfilePictureUrl { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Set only for accounts connected via the native "Instagram API with
        // Instagram Login" flow — those have no linked Facebook Page to
        // resolve a token from, so they carry their own directly.
        public string? NativeAccessToken { get; set; }
    }
}