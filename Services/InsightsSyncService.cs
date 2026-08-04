using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;

namespace SocialMediaPanel.Services
{
    /// <summary>
    /// Single place that writes reach/engagement numbers into the PostInsights
    /// table. Used by both InsightsController's webhook/cron-driven refresh and
    /// by PostService whenever a user opens a post's Stats tab — so the DB cache
    /// (used for cross-post reporting) stays warm from real usage too, not only
    /// from a background job.
    /// </summary>
    public class InsightsSyncService
    {
        private readonly AppDbContext _db;

        public InsightsSyncService(AppDbContext db)
        {
            _db = db;
        }

        public async Task UpsertPostInsightAsync(
            string postId,
            string? pageId,
            string platform,
            int? likes = null,
            int? comments = null,
            int? shares = null,
            int? reach = null,
            int? impressions = null,
            int? saves = null)
        {
            var insight = await _db.PostInsights
                .FirstOrDefaultAsync(p => p.PostId == postId && p.Platform == platform);

            if (insight == null)
            {
                insight = new PostInsight
                {
                    PostId = postId,
                    PageId = pageId,
                    Platform = platform,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.PostInsights.Add(insight);
            }

            if (likes.HasValue && likes.Value > 0) insight.LikesCount = likes.Value;
            if (comments.HasValue && comments.Value > 0) insight.CommentsCount = comments.Value;
            if (shares.HasValue && shares.Value > 0) insight.SharesCount = shares.Value;
            if (reach.HasValue && reach.Value > 0) insight.Reach = reach.Value;
            if (impressions.HasValue && impressions.Value > 0) insight.Impressions = impressions.Value;
            if (saves.HasValue && saves.Value > 0) insight.SavesCount = saves.Value;
            if (!string.IsNullOrEmpty(pageId)) insight.PageId = pageId;

            insight.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        // ── Overview aggregation for the Reports page ──────────────────────
        public async Task<InsightsOverview> GetOverviewAsync(IEnumerable<string> pageIds, int? userId = null)
        {
            var pageIdList = pageIds.ToList();

            var insights = await _db.PostInsights
                .Where(p => pageIdList.Contains(p.PageId!))
                .ToListAsync();

            var overview = new InsightsOverview
            {
                TotalReach = insights.Sum(i => i.Reach),
                TotalImpressions = insights.Sum(i => i.Impressions),
                TotalLikes = insights.Sum(i => i.LikesCount),
                TotalComments = insights.Sum(i => i.CommentsCount),
                TotalShares = insights.Sum(i => i.SharesCount),
                TotalSaves = insights.Sum(i => i.SavesCount),
                PostsTracked = insights.Count,
                TopPosts = insights
                    .OrderByDescending(i => i.LikesCount + i.CommentsCount + i.SharesCount)
                    .Take(5)
                    .Select(i => new TopPostInsight
                    {
                        PostId = i.PostId,
                        Platform = i.Platform,
                        Engagement = i.LikesCount + i.CommentsCount + i.SharesCount,
                        Reach = i.Reach,
                        UpdatedAt = i.UpdatedAt
                    })
                    .ToList(),
                // Per-platform breakdown — same PostInsights rows, grouped.
                PlatformBreakdown = insights
                    .GroupBy(i => i.Platform)
                    .Select(g => new PlatformBreakdownItem
                    {
                        Platform = g.Key,
                        PostsTracked = g.Count(),
                        Reach = g.Sum(i => i.Reach),
                        Engagement = g.Sum(i => i.LikesCount + i.CommentsCount + i.SharesCount)
                    })
                    .OrderByDescending(p => p.Engagement)
                    .ToList()
            };

            // This-week vs last-week engagement trend (by UpdatedAt — when we
            // last synced each post's numbers, not when it was originally posted).
            var now = DateTime.UtcNow;
            var thisWeekStart = now.AddDays(-7);
            var lastWeekStart = now.AddDays(-14);
            overview.EngagementThisWeek = insights.Where(i => i.UpdatedAt >= thisWeekStart)
                .Sum(i => i.LikesCount + i.CommentsCount + i.SharesCount);
            overview.EngagementLastWeek = insights.Where(i => i.UpdatedAt >= lastWeekStart && i.UpdatedAt < thisWeekStart)
                .Sum(i => i.LikesCount + i.CommentsCount + i.SharesCount);

            // Twitter/LinkedIn don't have a reach/engagement table today — show
            // activity counts instead of fabricating engagement numbers we
            // don't actually have.
            if (userId.HasValue)
            {
                overview.TwitterPostsThisMonth = await _db.TweetsPosted
                    .CountAsync(t => t.UserId == userId.Value && t.CreatedAt >= now.AddDays(-30) && t.Status == "posted");
                overview.LinkedInPostsThisMonth = await _db.LinkedInPosts
                    .CountAsync(p => p.UserId == userId.Value.ToString() && p.CreatedAt >= now.AddDays(-30) && p.Status == "posted");
            }

            return overview;
        }

        // ── Best-time-to-post suggestion (Category A, phase 6) ─────────────
        // Buckets tracked posts by hour-of-day using the matching SocialPosts
        // row's created_at where one exists (posts published through this
        // app); falls back to PostInsights.UpdatedAt (last sync time) for
        // posts this app never published itself (e.g. organic posts that
        // existed on the Page before it was connected) — clearly an
        // approximation for those, but still better than no signal.
        public async Task<List<BestPostingTime>> GetBestPostingTimesAsync(IEnumerable<string> pageIds)
        {
            var pageIdList = pageIds.ToList();

            var insights = await _db.PostInsights
                .Where(p => pageIdList.Contains(p.PageId!))
                .ToListAsync();

            if (insights.Count == 0) return new List<BestPostingTime>();

            var socialPostsByPostId = await _db.SocialPosts
                .Where(p => insights.Select(i => i.PostId).Contains(p.post_id!))
                .ToDictionaryAsync(p => p.post_id!, p => p.created_at);

            var buckets = insights
                .Select(i => new
                {
                    When = socialPostsByPostId.TryGetValue(i.PostId, out var createdAt) ? createdAt : i.UpdatedAt,
                    Engagement = i.LikesCount + i.CommentsCount + i.SharesCount
                })
                .GroupBy(x => new { Day = x.When.DayOfWeek, Hour = x.When.Hour })
                .Select(g => new BestPostingTime
                {
                    DayOfWeek = g.Key.Day.ToString(),
                    Hour = g.Key.Hour,
                    AverageEngagement = g.Average(x => x.Engagement),
                    SampleSize = g.Count()
                })
                .OrderByDescending(b => b.AverageEngagement)
                .Take(3)
                .ToList();

            return buckets;
        }
    }

    public class BestPostingTime
    {
        public string DayOfWeek { get; set; } = "";
        public int Hour { get; set; }
        public double AverageEngagement { get; set; }
        public int SampleSize { get; set; }
        public string DisplayTime => $"{(Hour % 12 == 0 ? 12 : Hour % 12)}:00 {(Hour < 12 ? "AM" : "PM")}";
    }

    public class InsightsOverview
    {
        public int TotalReach { get; set; }
        public int TotalImpressions { get; set; }
        public int TotalLikes { get; set; }
        public int TotalComments { get; set; }
        public int TotalShares { get; set; }
        public int TotalSaves { get; set; }
        public int PostsTracked { get; set; }
        public int TotalEngagement => TotalLikes + TotalComments + TotalShares;
        public List<TopPostInsight> TopPosts { get; set; } = new();

        // Page-level snapshots (populated by ReportsController from live Graph calls)
        public int? FacebookFans { get; set; }
        public int? InstagramFollowers { get; set; }

        // Unified Analytics Dashboard additions (Category A, phase 3)
        public List<PlatformBreakdownItem> PlatformBreakdown { get; set; } = new();
        public int EngagementThisWeek { get; set; }
        public int EngagementLastWeek { get; set; }
        public int? TwitterPostsThisMonth { get; set; }
        public int? LinkedInPostsThisMonth { get; set; }
    }

    public class TopPostInsight
    {
        public string PostId { get; set; } = "";
        public string Platform { get; set; } = "";
        public int Engagement { get; set; }
        public int Reach { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class PlatformBreakdownItem
    {
        public string Platform { get; set; } = "";
        public int PostsTracked { get; set; }
        public int Reach { get; set; }
        public int Engagement { get; set; }
    }
}
