using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using System.Text.Json;

namespace SocialMediaPanel.Controllers
{
    public class InboxController : Controller
    {
        private readonly AppDbContext _db;

        public InboxController(AppDbContext db)
        {
            _db = db;
        }

        // ── Index — renders the Smart Inbox page ─────────────────────
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var items = await GetMergedItems();
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            ViewBag.InboxJson = JsonSerializer.Serialize(items, jsonOptions);

            return View("~/Views/Inbox/Index.cshtml");
        }

        // ── GetAll — AJAX reload ──────────────────────────────────────
        [HttpGet]
        [Route("Inbox/GetAll")]
        public async Task<IActionResult> GetAll()
        {
            var items = await GetMergedItems();
            return Json(items);
        }


        // ── Private: merge comments + messages into unified list ──────
        private async Task<List<InboxItem>> GetMergedItems()
        {
            var comments = await _db.PageComments
                .OrderByDescending(c => c.CommentTime ?? c.CreatedAt)
                .Select(c => new InboxItem
                {
                    Id = c.Id,
                    ItemType = "comment",
                    Platform = c.Platform,
                    SenderId = c.SenderId ?? "",
                    // Show name if available, otherwise show shortened sender ID
                    SenderName = !string.IsNullOrEmpty(c.SenderName)
                                    ? c.SenderName
                                    : !string.IsNullOrEmpty(c.SenderId)
                                        ? "User " + c.SenderId.Substring(Math.Max(0, c.SenderId.Length - 6))
                                        : "Unknown",
                    Message = c.Message ?? "",
                    PostId = c.PostId ?? "",
                    PageId = c.PageId ?? "",
                    CommentType = c.CommentType,
                    IsReplied = false,
                    Time = c.CommentTime ?? c.CreatedAt
                })
                .ToListAsync();

            var messages = await _db.PageMessages
                .OrderByDescending(m => m.MessageTime ?? m.CreatedAt)
                .Select(m => new InboxItem
                {
                    Id = m.Id,
                    ItemType = "message",
                    Platform = m.Platform,
                    SenderId = m.SenderId ?? "",
                    SenderName = !string.IsNullOrEmpty(m.SenderName)
                                    ? m.SenderName
                                    : !string.IsNullOrEmpty(m.SenderId)
                                        ? "User " + m.SenderId.Substring(Math.Max(0, m.SenderId.Length - 6))
                                        : "Unknown",
                    Message = m.MessageText ?? "",
                    PostId = "",
                    PageId = m.PageId ?? "",
                    CommentType = "message",
                    IsReplied = m.IsReplied,
                    Time = m.MessageTime ?? m.CreatedAt
                })
                .ToListAsync();

            // Merge and sort newest first
            return comments
                .Concat(messages)
                .OrderByDescending(x => x.Time)
                .ToList();
        }
    }

    // ── Unified inbox item (used for both comments + messages) ────────
    public class InboxItem
    {
        public int Id { get; set; }
        public string ItemType { get; set; } = "comment";  // "comment" | "message"
        public string Platform { get; set; } = "";
        public string SenderId { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string Message { get; set; } = "";
        public string PostId { get; set; } = "";
        public string PageId { get; set; } = "";
        public string CommentType { get; set; } = "comment";  // "comment" | "mention" | "live_comment"
        public bool IsReplied { get; set; }
        public DateTime Time { get; set; }
    }

    // ── Reply request body ─────────────────────────────────────────────
    public class ReplyRequest
    {
        public int ItemId { get; set; }
        public string ItemType { get; set; } = "comment";
        public string Message { get; set; } = "";
        public string Platform { get; set; } = "";
    }
}