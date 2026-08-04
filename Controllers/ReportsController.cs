using Microsoft.AspNetCore.Mvc;
using SocialMediaPanel.Services;

namespace SocialMediaPanel.Controllers
{
    /// <summary>
    /// Real Reports page — replaces the "Home/INProgress" placeholder the
    /// nav bar used to point at. Renders reach/engagement/follower data from
    /// InsightsController's /api/insights/overview endpoint.
    /// </summary>
    public class ReportsController : Controller
    {
        private readonly InsightsSyncService _insightsSync;
        private readonly ActivePageService _activePages;

        public ReportsController(InsightsSyncService insightsSync, ActivePageService activePages)
        {
            _insightsSync = insightsSync;
            _activePages = activePages;
        }

        [HttpGet]
        public IActionResult Index()
        {
            if (HttpContext.Session.GetInt32("UserId") == null)
                return RedirectToAction("Login", "Account");

            return View();
        }

        // ── CSV EXPORT — Reports (Category A, phase 4) ────────────────────
        [HttpGet]
        public async Task<IActionResult> Export()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return Unauthorized();

            var fbPages = await _activePages.GetAllFacebookPagesAsync(userId.Value);
            var igAccounts = await _activePages.GetAllInstagramAccountsAsync(userId.Value);
            var pageIds = fbPages.Select(p => p.page_id).Concat(igAccounts.Select(a => a.InstagramUserId)).Distinct();

            var overview = await _insightsSync.GetOverviewAsync(pageIds, userId.Value);

            var csv = CsvExportHelper.BuildCsv(
                new[] { "PostId", "Platform", "Engagement", "Reach", "UpdatedAt" },
                overview.TopPosts.Select(p => new object?[]
                {
                    p.PostId, p.Platform, p.Engagement, p.Reach, p.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                }));

            return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"reports-{DateTime.UtcNow:yyyyMMdd}.csv");
        }
    }
}
