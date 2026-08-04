using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;

namespace SocialMediaPanel.Controllers
{
    public class CalendarPostItem
    {
        public string Platform { get; set; } = "";
        public string? Message { get; set; }
        public string? PostId { get; set; }
        public string Status { get; set; } = "";
        public DateTime Date { get; set; }
    }

    /// <summary>
    /// Content Calendar — a month-grid view across every platform this app
    /// posts to, built entirely from existing tables (SocialPosts,
    /// LinkedInPosts, TweetsPosted). No new schema.
    /// </summary>
    public class CalendarController : Controller
    {
        private readonly AppDbContext _db;

        public CalendarController(AppDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<IActionResult> Index(int? year, int? month)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var today = DateTime.UtcNow;
            var y = year ?? today.Year;
            var m = month ?? today.Month;
            var monthStart = new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthEnd = monthStart.AddMonths(1);

            var items = new List<CalendarPostItem>();

            var socialPosts = await _db.SocialPosts
                .Where(p => p.user_id == userId.Value && p.created_at >= monthStart && p.created_at < monthEnd)
                .ToListAsync();
            items.AddRange(socialPosts.Select(p => new CalendarPostItem
            {
                Platform = p.platform,
                Message = p.message,
                PostId = p.post_id,
                Status = p.status ?? "published",
                Date = p.scheduled_at ?? p.created_at
            }));

            var linkedInPosts = await _db.LinkedInPosts
                .Where(p => p.UserId == userId.ToString() && p.CreatedAt >= monthStart && p.CreatedAt < monthEnd)
                .ToListAsync();
            items.AddRange(linkedInPosts.Select(p => new CalendarPostItem
            {
                Platform = "linkedin",
                Message = p.PostText,
                PostId = p.PostId,
                Status = p.Status ?? "posted",
                Date = p.PostedAt ?? p.CreatedAt
            }));

            var tweets = await _db.TweetsPosted
                .Where(t => t.UserId == userId.Value && t.CreatedAt >= monthStart && t.CreatedAt < monthEnd)
                .ToListAsync();
            items.AddRange(tweets.Select(t => new CalendarPostItem
            {
                Platform = "twitter",
                Message = t.TweetText,
                PostId = t.TweetId,
                Status = t.Status ?? "posted",
                Date = t.PostedAt ?? t.CreatedAt
            }));

            ViewBag.Year = y;
            ViewBag.Month = m;
            ViewBag.MonthName = monthStart.ToString("MMMM yyyy");
            ViewBag.PrevYear = monthStart.AddMonths(-1).Year;
            ViewBag.PrevMonth = monthStart.AddMonths(-1).Month;
            ViewBag.NextYear = monthStart.AddMonths(1).Year;
            ViewBag.NextMonth = monthStart.AddMonths(1).Month;

            return View(items.OrderBy(i => i.Date).ToList());
        }
    }
}
