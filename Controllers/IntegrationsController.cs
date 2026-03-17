using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using SocialMediaPanel.Services;

namespace SocialMediaPanel.Controllers
{
    public class IntegrationsController : Controller
    {
        private readonly FacebookService _facebook;
        private readonly IDataProtector _protector;

        public IntegrationsController(
             FacebookService facebook,
             IDataProtectionProvider dataProtection)
        {
            _facebook = facebook;
            _protector = dataProtection.CreateProtector("Integrations.Facebook.OAuthState");
        }


        [HttpGet]
        [Route("Integrations/Modal/{platform}")]
        public IActionResult Modal(string platform)
        {
            switch (platform.ToLower())
            {
                case "facebook": return _FacebookModal();
                default: return NotFound($"No modal for: {platform}");
            }
        }

        private IActionResult _FacebookModal()
        {
            string oauthUrl = "#";
            string? oauthErr = null;

            try
            {
                //var state = Guid.NewGuid().ToString("N");
                //try { HttpContext.Session.SetString("fb_oauth_state", state); } catch { }
                var userId = User.Identity?.Name ?? "anonymous";
                var raw = $"{userId}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                var state = _protector.Protect(raw);

                oauthUrl = _facebook.BuildOAuthUrl(state);
            }
            catch
            {
                oauthErr = "Facebook App ID / Secret not configured in appsettings.json";
            }

            ViewData["OAuthUrl"] = oauthUrl;
            ViewData["OAuthError"] = oauthErr;
            ViewData["IsReconnecting"] = false;
            ViewData["ConnectedPageName"] = null;

            return PartialView("~/Views/Shared/Integrations/_Facebook.cshtml");
        }

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

            if (string.IsNullOrEmpty(state))
            {
                TempData["IntegrationError"] = "Missing state parameter. Please try again.";
                return RedirectToAction("Index", "Dashboard");
            }

            try
            {
                var raw = _protector.Unprotect(state);  // throws if tampered
                var parts = raw.Split('|');

                // Validate timestamp — reject tokens older than 10 minutes
                if (parts.Length == 2 &&
                    long.TryParse(parts[1], out var ts))
                {
                    var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts;
                    if (age > 600) 
                    {
                        TempData["IntegrationError"] = "OAuth session expired. Please try again.";
                        return RedirectToAction("Index", "Dashboard");
                    }
                }
            }
            catch
            {
                TempData["IntegrationError"] = "Invalid state parameter. Please try again.";
                return RedirectToAction("Index", "Dashboard");
            }

            if (string.IsNullOrEmpty(code))
            {
                TempData["IntegrationError"] = "No authorisation code received from Facebook.";
                return RedirectToAction("Index", "Dashboard");
            }

            try
            {
                var tokenResult = await _facebook.ExchangeCodeAsync(code);
                var pages = await _facebook.GetManagedPagesAsync(tokenResult.LongLivedToken);

                // TODO: save tokenResult + pages to your database
                // await _db.SaveFacebookIntegrationAsync(User.GetUserId(), tokenResult, pages);

                TempData["IntegrationSuccess"] = "facebook";
                TempData["FacebookPageName"] = pages.FirstOrDefault()?.Name ?? "your page";
            }
            catch (Exception ex)
            {
                TempData["IntegrationError"] = "Connection failed: " + ex.Message;
            }

            return RedirectToAction("Index", "Dashboard");
        }

        [HttpPost]
        [Route("Integrations/Disconnect/{platform}")]
        [ValidateAntiForgeryToken]
        public IActionResult Disconnect(string platform)
        {
            return Json(new { success = true });
        }
    }
}