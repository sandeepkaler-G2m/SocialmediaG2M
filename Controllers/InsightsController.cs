using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using System.Text.Json;

namespace SocialMediaPanel.Controllers
{
    // ══════════════════════════════════════════════════════════
    // InsightsController
    // Reach, Impressions, Saves Graph API se fetch karta hai
    //
    // Endpoints:
    // GET /api/insights/refresh          — Saari posts update
    // GET /api/insights/post/{postId}    — Single post update
    // GET /api/insights/all              — DB se saare insights
    // GET /api/insights/page/{pageId}    — Page ki saari posts
    // ══════════════════════════════════════════════════════════
    [Route("api/insights")]
    [ApiController]
    public class InsightsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<InsightsController> _logger;
        private readonly SocialMediaPanel.Services.InsightsSyncService _insightsSync;
        private readonly SocialMediaPanel.Services.ActivePageService _activePages;
        private readonly SocialMediaPanel.Services.FacebookService _facebookService;
        private readonly SocialMediaPanel.Services.InstagramService _instagramService;

        // Confirmed live against the current Graph API version (v23.0, the
        // version this app's tokens get served): post_impressions,
        // post_impressions_unique and post_engaged_users are ALL rejected
        // ("The value must be a valid insights metric") — Meta has removed
        // per-post reach/impressions for organic (unboosted) Page posts
        // entirely. There is no current replacement metric that gives reach/
        // impressions for an organic post, so FetchAndUpdateInsight skips the
        // Facebook branch's API call below rather than making a call that
        // always fails.
        private const string FB_METRICS = "post_impressions,post_impressions_unique,post_engaged_users";

        // Instagram Media metrics — "impressions" and "likes_count"/"comments_count"
        // removed: impressions errors on current-version media ("no longer
        // supported for the queried media"), and likes_count/comments_count
        // were never valid insight metric names (that's "likes"/"comments",
        // and this method doesn't consume them anyway — see WebhookController
        // for likes/comments, which come from webhook events instead).
        private const string IG_METRICS = "reach,saved";

        public InsightsController(
            AppDbContext context,
            IHttpClientFactory httpClientFactory,
            ILogger<InsightsController> logger,
            SocialMediaPanel.Services.InsightsSyncService insightsSync,
            SocialMediaPanel.Services.ActivePageService activePages,
            SocialMediaPanel.Services.FacebookService facebookService,
            SocialMediaPanel.Services.InstagramService instagramService)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _insightsSync = insightsSync;
            _activePages = activePages;
            _facebookService = facebookService;
            _instagramService = instagramService;
        }

        // ══════════════════════════════════════════════════════
        // GET /api/insights/overview
        // Aggregated reach/engagement + page-level snapshot for the
        // logged-in user's connected pages/accounts — feeds the Reports page.
        // ══════════════════════════════════════════════════════
        [HttpGet("overview")]
        public async Task<IActionResult> Overview()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return Unauthorized(new { success = false, message = "Not logged in" });

            var fbPages = await _activePages.GetAllFacebookPagesAsync(userId.Value);
            var igAccounts = await _activePages.GetAllInstagramAccountsAsync(userId.Value);

            var pageIds = fbPages.Select(p => p.page_id)
                .Concat(igAccounts.Select(a => a.InstagramUserId))
                .Distinct()
                .ToList();

            var overview = await _insightsSync.GetOverviewAsync(pageIds, userId.Value);

            // Page-level snapshot from the active Facebook Page + Instagram account
            var activeFb = await _activePages.GetActiveFacebookPageAsync(userId.Value);
            if (activeFb != null)
            {
                try
                {
                    var fbInsights = await _facebookService.GetPageInsightsAsync(activeFb.page_id, activeFb.page_access_token);
                    overview.FacebookFans = fbInsights.Fans;
                }
                catch { }
            }

            var activeIg = await _activePages.GetActiveInstagramAccountAsync(userId.Value);
            if (activeIg != null)
            {
                var linkedPage = await _activePages.GetLinkedPageForInstagramAsync(userId.Value, activeIg.InstagramUserId);
                if (linkedPage != null)
                {
                    try
                    {
                        var igInsights = await _instagramService.GetAccountInsightsAsync(activeIg.InstagramUserId, linkedPage.page_access_token);
                        overview.InstagramFollowers = igInsights.Followers;
                    }
                    catch { }
                }
            }

            return Ok(new { success = true, data = overview });
        }

        // ══════════════════════════════════════════════════════
        // GET /api/insights/best-times
        // Top hour/day-of-week combos by historical average engagement.
        // ══════════════════════════════════════════════════════
        [HttpGet("best-times")]
        public async Task<IActionResult> BestTimes()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return Unauthorized(new { success = false, message = "Not logged in" });

            var fbPages = await _activePages.GetAllFacebookPagesAsync(userId.Value);
            var igAccounts = await _activePages.GetAllInstagramAccountsAsync(userId.Value);
            var pageIds = fbPages.Select(p => p.page_id).Concat(igAccounts.Select(a => a.InstagramUserId)).Distinct();

            var bestTimes = await _insightsSync.GetBestPostingTimesAsync(pageIds);
            return Ok(new { success = true, data = bestTimes });
        }

        // ══════════════════════════════════════════════════════
        // GET /api/insights/refresh
        // DB mein existing posts + Graph API se nai posts update
        // ══════════════════════════════════════════════════════
        [HttpGet("refresh")]
        public async Task<IActionResult> RefreshAll()
        {
            _logger.LogInformation("Insights refresh started...");

            var accounts = await _context.ConnectedAccounts
                .Where(a => a.IsActive)
                .ToListAsync();

            if (!accounts.Any())
                return BadRequest(new { success = false, message = "Koi connected account nahi hai" });

            int updated = 0, failed = 0;

            foreach (var account in accounts)
            {
                // DB mein jo posts hain unhe update karo
                var existingPosts = await _context.PostInsights
                    .Where(p => p.PageId == account.AccountId)
                    .ToListAsync();

                foreach (var post in existingPosts)
                {
                    var result = await FetchAndUpdateInsight(
                        post.PostId,
                        account.AccountId,
                        account.AccessToken!,
                        account.Platform);

                    if (result) updated++;
                    else failed++;
                }

                // Graph API se nai posts bhi lo
                var fetchedPostIds = await FetchPagePosts(
                    account.AccountId,
                    account.AccessToken!);

                foreach (var postId in fetchedPostIds)
                {
                    // Pehle se update ho gayi to skip
                    if (existingPosts.Any(p => p.PostId == postId)) continue;

                    var result = await FetchAndUpdateInsight(
                        postId,
                        account.AccountId,
                        account.AccessToken!,
                        account.Platform);

                    if (result) updated++;
                    else failed++;
                }
            }

            _logger.LogInformation(
                "Insights refresh done — updated={U} failed={F}", updated, failed);

            return Ok(new
            {
                success = true,
                message = $"Insights refresh complete",
                updated = updated,
                failed = failed,
                time = DateTime.UtcNow
            });
        }

        // ══════════════════════════════════════════════════════
        // GET /api/insights/post/{postId}
        // Single post ka insight fetch + update
        // ══════════════════════════════════════════════════════
        [HttpGet("post/{postId}")]
        public async Task<IActionResult> RefreshSinglePost(string postId)
        {
            _logger.LogInformation("Single post insight fetch — postId={PostId}", postId);

            var account = await _context.ConnectedAccounts
                .Where(a => a.IsActive)
                .FirstOrDefaultAsync();

            if (account == null)
                return BadRequest(new
                {
                    success = false,
                    message = "Connected account nahi mila — pehle account connect karo"
                });

            var result = await FetchAndUpdateInsight(
                postId,
                account.AccountId,
                account.AccessToken!,
                account.Platform);

            if (!result)
                return BadRequest(new
                {
                    success = false,
                    message = "Insight fetch failed — Visual Studio Output mein logs dekho"
                });

            var insight = await _context.PostInsights
                .Where(p => p.PostId == postId)
                .FirstOrDefaultAsync();

            return Ok(new { success = true, data = insight });
        }

        // ══════════════════════════════════════════════════════
        // GET /api/insights/all
        // DB mein saved saare insights
        // ══════════════════════════════════════════════════════
        [HttpGet("all")]
        public async Task<IActionResult> GetAll()
        {
            var insights = await _context.PostInsights
                .OrderByDescending(p => p.UpdatedAt)
                .Take(50)
                .ToListAsync();

            return Ok(new
            {
                success = true,
                count = insights.Count,
                data = insights
            });
        }

        // ══════════════════════════════════════════════════════
        // GET /api/insights/page/{pageId}
        // Page ki recent posts fetch + insights update
        // ══════════════════════════════════════════════════════
        [HttpGet("page/{pageId}")]
        public async Task<IActionResult> GetByPage(string pageId)
        {
            var account = await _context.ConnectedAccounts
                .Where(a => a.AccountId == pageId && a.IsActive)
                .FirstOrDefaultAsync();

            if (account == null)
                return NotFound(new { success = false, message = "Page nahi mila connected accounts mein" });

            // Graph API se latest posts lo
            var postIds = await FetchPagePosts(pageId, account.AccessToken!);

            int updated = 0, failed = 0;
            foreach (var postId in postIds)
            {
                var result = await FetchAndUpdateInsight(
                    postId, pageId, account.AccessToken!, account.Platform);

                if (result) updated++;
                else failed++;
            }

            var insights = await _context.PostInsights
                .Where(p => p.PageId == pageId)
                .OrderByDescending(p => p.UpdatedAt)
                .ToListAsync();

            return Ok(new
            {
                success = true,
                updated = updated,
                failed = failed,
                count = insights.Count,
                data = insights
            });
        }

        // ══════════════════════════════════════════════════════
        // PRIVATE — Page ki recent posts lo
        // GET /{PAGE_ID}/posts?fields=id&limit=25
        // ══════════════════════════════════════════════════════
        private async Task<List<string>> FetchPagePosts(string pageId, string accessToken)
        {
            var postIds = new List<string>();

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);

                var url = $"https://graph.facebook.com/v19.0/{pageId}/posts" +
                          $"?fields=id,message,created_time" +
                          $"&limit=25" +
                          $"&access_token={accessToken}";

                var response = await client.GetStringAsync(url);

                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                // Error check
                if (root.TryGetProperty("error", out var error))
                {
                    var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "Unknown";
                    _logger.LogWarning("Page posts fetch error: {Msg}", msg);
                    return postIds;
                }

                if (root.TryGetProperty("data", out var data))
                {
                    foreach (var post in data.EnumerateArray())
                    {
                        if (post.TryGetProperty("id", out var id))
                            postIds.Add(id.GetString()!);
                    }
                }

                _logger.LogInformation(
                    "Page posts fetched — pageId={PageId} count={Count}",
                    pageId, postIds.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("FetchPagePosts failed: {Msg}", ex.Message);
            }

            return postIds;
        }

        // ══════════════════════════════════════════════════════
        // PRIVATE — Single post ka insight fetch + DB update
        //
        // Facebook valid metrics:
        //   post_impressions         — Total impressions
        //   post_impressions_unique  — Unique reach
        //   post_engaged_users       — Engaged users
        //
        // Instagram valid metrics:
        //   reach, impressions, saved, likes_count, comments_count
        // ══════════════════════════════════════════════════════
        private async Task<bool> FetchAndUpdateInsight(
            string postId,
            string? pageId,
            string accessToken,
            string platform)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);

                int reach = 0, impressions = 0, saves = 0;

                if (platform == "facebook")
                {
                    // Meta no longer exposes reach/impressions for organic Page
                    // posts (see FB_METRICS comment) — nothing to fetch here.
                    // Likes/comments/shares for this post already arrive via
                    // WebhookController's feed-change events. Treat as a
                    // successful no-op rather than calling an endpoint that's
                    // guaranteed to error.
                    return true;
                }
                else if (platform == "instagram")
                {
                    // ── Instagram Media Insights ──
                    var url = $"https://graph.facebook.com/v19.0/{postId}/insights" +
                              $"?metric={IG_METRICS}" +
                              $"&access_token={accessToken}";

                    _logger.LogInformation(
                        "IG insight fetch — postId={PostId}", postId);

                    var response = await client.GetStringAsync(url);

                    _logger.LogInformation(
                        "IG response preview: {Preview}",
                        response[..Math.Min(300, response.Length)]);

                    using var doc = JsonDocument.Parse(response);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("error", out var error))
                    {
                        var errMsg = error.TryGetProperty("message", out var em)
                            ? em.GetString() : "Unknown";
                        _logger.LogWarning("IG insight API error: {ErrMsg}", errMsg);
                        return false;
                    }

                    if (root.TryGetProperty("data", out var data))
                    {
                        foreach (var metric in data.EnumerateArray())
                        {
                            var name = metric.TryGetProperty("name", out var n)
                                ? n.GetString() : "";

                            // IG metrics format alag hai
                            int val = 0;
                            if (metric.TryGetProperty("values", out var vals)
                                && vals.GetArrayLength() > 0
                                && vals[0].TryGetProperty("value", out var vv)
                                && vv.ValueKind == JsonValueKind.Number)
                            {
                                val = vv.GetInt32();
                            }
                            else if (metric.TryGetProperty("value", out var directVal)
                                && directVal.ValueKind == JsonValueKind.Number)
                            {
                                val = directVal.GetInt32();
                            }

                            switch (name)
                            {
                                case "reach": reach = val; break;
                                case "impressions": impressions = val; break;
                                case "saved": saves = val; break;
                            }
                        }
                    }
                }

                // ── DB mein update karo — shared upsert (InsightsSyncService) ──
                await _insightsSync.UpsertPostInsightAsync(
                    postId, pageId, platform,
                    reach: reach, impressions: impressions, saves: saves);

                _logger.LogInformation(
                    "Insight updated ✅ postId={PostId} reach={R} impressions={I} engaged={S}",
                    postId, reach, impressions, saves);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "FetchAndUpdateInsight failed — postId={PostId}: {Msg}",
                    postId, ex.Message);
                return false;
            }
        }
    }
}