using Microsoft.AspNetCore.Mvc;
using SocialMediaPanel.Services;

namespace SocialMediaPanel.Controllers
{
    /// <summary>
    /// Ads Management — ad accounts, campaigns, insights, and boosting an
    /// existing organic post. Requires ads_management (see MetaScopes.Full).
    /// New campaigns are always created PAUSED; the user activates explicitly.
    /// </summary>
    public class AdsController : Controller
    {
        private readonly FacebookAdsService _ads;
        private readonly PostService _postService;
        private readonly AuditLogService _audit;

        public AdsController(FacebookAdsService ads, PostService postService, AuditLogService audit)
        {
            _ads = ads;
            _postService = postService;
            _audit = audit;
        }

        private int? GetUserId() => HttpContext.Session.GetInt32("UserId");
        private string GetUserEmail() => HttpContext.Session.GetString("UserEmail") ?? "";

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (GetUserId() == null) return RedirectToAction("Login", "Account");

            var userToken = await _postService.GetTokenAsync(GetUserEmail());
            ViewBag.HasToken = !string.IsNullOrEmpty(userToken);

            var accounts = string.IsNullOrEmpty(userToken)
                ? new List<AdAccountInfo>()
                : await _ads.GetAdAccountsAsync(userToken);

            return View(accounts);
        }

        // ── Lightweight JSON list for the "Boost" widget in the Posts modal ──
        [HttpGet]
        [Route("Ads/AdAccounts")]
        public async Task<IActionResult> AdAccountsJson()
        {
            if (GetUserId() == null) return Unauthorized();

            var userToken = await _postService.GetTokenAsync(GetUserEmail());
            if (string.IsNullOrEmpty(userToken))
                return Json(new { success = false, message = "Facebook not connected." });

            var accounts = await _ads.GetAdAccountsAsync(userToken);
            return Json(new { success = true, data = accounts });
        }

        [HttpGet]
        [Route("Ads/Campaigns/{adAccountId}")]
        public async Task<IActionResult> Campaigns(string adAccountId)
        {
            if (GetUserId() == null) return Unauthorized();

            var userToken = await _postService.GetTokenAsync(GetUserEmail());
            if (string.IsNullOrEmpty(userToken))
                return Json(new { success = false, message = "Facebook not connected." });

            var campaigns = await _ads.GetCampaignsAsync(adAccountId, userToken);
            return Json(new { success = true, data = campaigns });
        }

        [HttpGet]
        [Route("Ads/Insights/{campaignId}")]
        public async Task<IActionResult> Insights(string campaignId)
        {
            if (GetUserId() == null) return Unauthorized();

            var userToken = await _postService.GetTokenAsync(GetUserEmail());
            if (string.IsNullOrEmpty(userToken))
                return Json(new { success = false, message = "Facebook not connected." });

            var insight = await _ads.GetCampaignInsightsAsync(campaignId, userToken);
            return Json(new { success = true, data = insight });
        }

        // ── Boost an existing post ──────────────────────────────────────
        [HttpPost]
        [Route("Ads/BoostPost")]
        public async Task<IActionResult> BoostPost(string adAccountId, string pageId, string postId, decimal dailyBudget)
        {
            if (GetUserId() == null) return Json(new { success = false, message = "Not logged in" });

            if (string.IsNullOrEmpty(adAccountId) || string.IsNullOrEmpty(pageId) || string.IsNullOrEmpty(postId))
                return Json(new { success = false, message = "adAccountId, pageId and postId are required." });

            // Meta enforces a currency-specific minimum daily budget (confirmed
            // live for INR: "must be more than ₹97.09"). 100 clears that with
            // room to spare; still created PAUSED so nothing actually spends.
            if (dailyBudget <= 0) dailyBudget = 100;

            var userToken = await _postService.GetTokenAsync(GetUserEmail());
            if (string.IsNullOrEmpty(userToken))
                return Json(new { success = false, message = "Facebook not connected." });

            try
            {
                var result = await _ads.BoostPostAsync(adAccountId, pageId, postId, dailyBudget, userToken);
                _audit.Log(GetUserId(), "ads.boost", $"postId={postId} adAccountId={adAccountId} dailyBudget={dailyBudget} campaignId={result.CampaignId}");
                return Json(new
                {
                    success = true,
                    message = "Boost campaign created (paused) — activate it from Ads Manager or the Activate button once reviewed.",
                    campaignId = result.CampaignId,
                    adSetId = result.AdSetId,
                    adId = result.AdId
                });
            }
            catch (PaymentMethodRequiredException ex)
            {
                return Json(new
                {
                    success = false,
                    needsPaymentMethod = true,
                    billingUrl = ex.BillingUrl,
                    message = "This ad account has no payment method on file. Add one on Meta, then retry."
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Boost failed: " + ex.Message });
            }
        }

        [HttpPost]
        [Route("Ads/Activate/{campaignId}")]
        public async Task<IActionResult> Activate(string campaignId, bool activate = true)
        {
            if (GetUserId() == null) return Json(new { success = false, message = "Not logged in" });

            var userToken = await _postService.GetTokenAsync(GetUserEmail());
            if (string.IsNullOrEmpty(userToken))
                return Json(new { success = false, message = "Facebook not connected." });

            var ok = await _ads.SetCampaignStatusAsync(campaignId, activate ? "ACTIVE" : "PAUSED", userToken);
            return Json(new { success = ok, message = ok ? (activate ? "Campaign activated." : "Campaign paused.") : "Failed to update campaign." });
        }
    }
}
