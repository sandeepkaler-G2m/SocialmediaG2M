using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using System.Text;
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

        // ── Reply endpoint ───────────────────────────────────────────
        [HttpPost]
        [Route("Inbox/Reply")]
        public async Task<IActionResult> Reply([FromBody] ReplyRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Message))
                return BadRequest(new { success = false, error = "Message is required." });

            var reply = new PageReply
            {
                SourceId = req.ItemId,
                SourceType = req.ItemType,
                Platform = req.Platform,
                ReplyText = req.Message,
                CreatedAt = DateTime.UtcNow
            };

            bool apiSuccess = false;
            string apiError = "";

            try
            {
                if (req.ItemType == "message")
                {
                    // ── Reply to DM ──────────────────────────────────
                    var msg = await _db.PageMessages.FindAsync(req.ItemId);
                    if (msg == null)
                        return NotFound(new { success = false, error = "Message not found." });

                    reply.PageId = msg.PageId ?? "";
                    reply.PostId = "";

                    if (req.Platform == "facebook" || req.Platform == "messenger")
                    {
                        var result = await SendFacebookDMAsync(msg.SenderId ?? "", req.Message, msg.PageId ?? "");
                        apiSuccess = result.Success;
                        apiError = result.Error;
                    }
                    else if (req.Platform == "instagram" || req.Platform == "instagram_dm")
                    {
                        var result = await SendInstagramDMAsync(msg.SenderId ?? "", req.Message, msg.PageId ?? "");
                        apiSuccess = result.Success;
                        apiError = result.Error;
                    }

                    // Mark original message as replied
                    if (apiSuccess)
                    {
                        msg.IsReplied = true;
                        _db.PageMessages.Update(msg);
                    }
                }
                else
                {
                    // ── Reply to comment ─────────────────────────────
                    var comment = await _db.PageComments.FindAsync(req.ItemId);
                    if (comment == null)
                        return NotFound(new { success = false, error = "Comment not found." });

                    reply.PageId = comment.PageId ?? "";
                    reply.PostId = comment.PostId ?? "";

                    if (req.Platform == "facebook" || req.Platform == "messenger")
                    {
                        var result = await SendFacebookCommentReplyAsync(comment.CommentId ?? "", req.Message, comment.PostId ?? "");

                        apiSuccess = result.Success;
                        apiError = result.Error;
                    }
                    else if (req.Platform == "instagram" || req.Platform == "instagram_dm")
                    {
                        var result = await SendFacebookCommentReplyAsync(comment.CommentId ?? "", req.Message, comment.PostId ?? "");
                        apiError = result.Error;
                    }
                }
            }
            catch (Exception ex)
            {
                apiError = ex.Message;
                apiSuccess = false;
            }

            // ── Save reply record to DB regardless (log successes + failures) ──
            reply.IsSuccess = apiSuccess;
            reply.ErrorMessage = apiSuccess ? null : apiError;
            _db.PageReplies.Add(reply);
            await _db.SaveChangesAsync();

            if (!apiSuccess)
                return Ok(new { success = false, error = apiError });

            return Ok(new { success = true, replyId = reply.Id });
        }

        [HttpGet]
        [Route("Inbox/GetPostPreview")]
        public async Task<IActionResult> GetPostPreview(string postId, string pageId, string platform)
        {
            if (string.IsNullOrEmpty(postId))
                return Json(new { error = "No postId" });

            var token = await GetPageAccessTokenAsync(pageId, platform);
            if (string.IsNullOrEmpty(token))
                return Json(new { error = "No token" });

            var client = new HttpClient();
            var fields = "id,message,story,name,description,full_picture,picture,created_time";
            var url = $"https://graph.facebook.com/v19.0/{postId}?fields={fields}&access_token={token}";

            var resp = await client.GetAsync(url);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return Json(new { error = json });

            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            return Json(new
            {
                id = root.TryGetProperty("id", out var id) ? id.GetString() : "",
                message = root.TryGetProperty("message", out var msg) ? msg.GetString() : "",
                story = root.TryGetProperty("story", out var st) ? st.GetString() : "",
                name = root.TryGetProperty("name", out var nm) ? nm.GetString() : "",
                description = root.TryGetProperty("description", out var desc) ? desc.GetString() : "",
                fullPicture = root.TryGetProperty("full_picture", out var fp) ? fp.GetString() : "",
                picture = root.TryGetProperty("picture", out var pic) ? pic.GetString() : "",
                createdTime = root.TryGetProperty("created_time", out var ct) ? ct.GetString() : ""
            });
        }

        // ── GetReplies — load existing replies for a thread ──────────
        [HttpGet]
        [Route("Inbox/GetReplies")]
        public async Task<IActionResult> GetReplies(int itemId, string itemType)
        {
            var replies = await _db.PageReplies
                .Where(r => r.SourceId == itemId && r.SourceType == itemType)
                .OrderBy(r => r.CreatedAt)
                .Select(r => new {
                    r.Id,
                    r.ReplyText,
                    r.IsSuccess,
                    r.ErrorMessage,
                    r.CreatedAt,
                    Sender = "You (Page)"
                })
                .ToListAsync();

            return Json(replies);
        }

        // ══ Facebook API helpers ═════════════════════════════════════

        private async Task<(bool Success, string Error)> SendFacebookCommentReplyAsync(
            string commentId, string message, string pageId)
        {
            // Get page access token from DB or config
            var token = await GetPageAccessTokenAsync(pageId, "facebook");
            if (string.IsNullOrEmpty(token))
                return (false, "No Facebook access token found for this page.");

            var client = new HttpClient();
            var url = $"https://graph.facebook.com/v19.0/{commentId}/comments";
            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("message",      message),
                new KeyValuePair<string,string>("access_token", token)
            });

            var resp = await client.PostAsync(url, body);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return (false, $"Facebook API error: {json}");

            return (true, "");
        }

        private async Task<(bool Success, string Error)> SendFacebookDMAsync(
            string recipientId, string message, string pageId)
        {
            var token = await GetPageAccessTokenAsync(pageId, "facebook");
            if (string.IsNullOrEmpty(token))
                return (false, "No Facebook access token found for this page.");

            var client = new HttpClient();
            var url = $"https://graph.facebook.com/v19.0/me/messages?access_token={token}";
            var payload = new
            {
                recipient = new { id = recipientId },
                message = new { text = message }
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(url, content);
            var respJson = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return (false, $"Facebook Messenger API error: {respJson}");

            return (true, "");
        }

        // ══ Instagram API helpers ════════════════════════════════════

        private async Task<(bool Success, string Error)> SendInstagramCommentReplyAsync(
            string commentId, string message, string pageId)
        {
            var token = await GetPageAccessTokenAsync(pageId, "instagram");
            if (string.IsNullOrEmpty(token))
                return (false, "No Instagram access token found for this page.");

            var client = new HttpClient();
            var url = $"https://graph.facebook.com/v19.0/{commentId}/replies";
            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("message",      message),
                new KeyValuePair<string,string>("access_token", token)
            });

            var resp = await client.PostAsync(url, body);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return (false, $"Instagram API error: {json}");

            return (true, "");
        }

        private async Task<(bool Success, string Error)> SendInstagramDMAsync(
            string recipientId, string message, string pageId)
        {
            var token = await GetPageAccessTokenAsync(pageId, "instagram");
            if (string.IsNullOrEmpty(token))
                return (false, "No Instagram access token found for this page.");

            var client = new HttpClient();
            var url = $"https://graph.facebook.com/v19.0/me/messages?access_token={token}";
            var payload = new
            {
                recipient = new { id = recipientId },
                message = new { text = message }
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(url, content);
            var respJson = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return (false, $"Instagram DM API error: {respJson}");

            return (true, "");
        }

        // ── Get page access token from DB ────────────────────────────
        private async Task<string?> GetPageAccessTokenAsync(string pageId, string platform)
        {
            var userid = HttpContext.Session.GetInt32("UserId");

            var account = await _db.UserTokens
                .Where(a => a.userId == userid.ToString())
                .Select(a => a.facebooktoken)
                .FirstOrDefaultAsync();

            return account;
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
        public string PostName { get; set; } = "";
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