using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;

namespace SocialMediaPanel.Controllers
{
    /// <summary>
    /// Lightweight "what's new since I last checked" badge — no Notifications
    /// table. "Last seen" lives in Session (per login, resets on logout),
    /// compared against timestamps already stored on Leads/PageComments/
    /// PageMessages. Good enough for a single-operator badge, not a durable
    /// per-notification read/unread system.
    /// </summary>
    [Route("api/notifications")]
    [ApiController]
    public class NotificationsController : ControllerBase
    {
        private readonly AppDbContext _db;

        public NotificationsController(AppDbContext db)
        {
            _db = db;
        }

        private DateTime GetLastSeen()
        {
            var raw = HttpContext.Session.GetString("NotificationsLastSeen");
            if (raw != null && DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt;
            // First check this session — default to last 24h so a fresh login
            // doesn't silently swallow anything that arrived just before it.
            var fallback = DateTime.UtcNow.AddHours(-24);
            HttpContext.Session.SetString("NotificationsLastSeen", fallback.ToString("o"));
            return fallback;
        }

        [HttpGet("count")]
        public async Task<IActionResult> Count()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return Unauthorized(new { success = false, count = 0 });

            var lastSeen = GetLastSeen();

            var pageIds = await _db.FacebookPages
                .Where(p => p.user_id == userId.ToString())
                .Select(p => p.page_id)
                .Distinct()
                .ToListAsync();
            var igIds = await _db.InstagramAccounts
                .Where(a => a.UserId == userId.ToString())
                .Select(a => a.InstagramUserId)
                .ToListAsync();
            var allIds = pageIds.Concat(igIds).Distinct().ToList();

            var newLeads = await _db.Leads.CountAsync(l => allIds.Contains(l.PageId!) && l.CreatedAt > lastSeen);
            var newComments = await _db.PageComments.CountAsync(c => allIds.Contains(c.PageId!) && (c.CommentTime ?? c.CreatedAt) > lastSeen);
            var newMessages = await _db.PageMessages.CountAsync(m => allIds.Contains(m.PageId!) && (m.MessageTime ?? m.CreatedAt) > lastSeen);

            return Ok(new
            {
                success = true,
                count = newLeads + newComments + newMessages,
                newLeads,
                newComments,
                newMessages
            });
        }

        [HttpPost("mark-seen")]
        public IActionResult MarkSeen()
        {
            HttpContext.Session.SetString("NotificationsLastSeen", DateTime.UtcNow.ToString("o"));
            return Ok(new { success = true });
        }
    }
}
