using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Hubs;
using SocialMediaPanel.Models;
using SocialMediaPanel.Services;
using System.Text;
using System.Text.Json;

namespace SocialMediaPanel.Controllers
{
    public class InboxController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ActivePageService _activePages;
        private readonly NativeInstagramService _nativeInstagram;
        private readonly IHubContext<InboxHub> _hub;

        public InboxController(AppDbContext db, ActivePageService activePages, NativeInstagramService nativeInstagram, IHubContext<InboxHub> hub)
        {
            _db = db;
            _activePages = activePages;
            _nativeInstagram = nativeInstagram;
            _hub = hub;
        }

        // Native-login IG accounts (direct "Instagram API with Instagram
        // Login") carry their own token — check that before falling back to
        // the Facebook-Page-linked resolution used everywhere else.
        private async Task<string?> GetNativeInstagramTokenAsync(string? igUserId)
        {
            if (string.IsNullOrEmpty(igUserId)) return null;
            var account = await _db.InstagramAccounts
                .FirstOrDefaultAsync(a => a.InstagramUserId == igUserId && a.NativeAccessToken != null);
            return account?.NativeAccessToken;
        }

        // ── Index — renders the Smart Inbox page ─────────────────────
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (HttpContext.Session.GetInt32("UserId") == null)
                return RedirectToAction("Login", "Account");

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
            if (HttpContext.Session.GetInt32("UserId") == null)
                return Unauthorized();

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
                        var result = await SendFacebookCommentReplyAsync(comment.CommentId ?? "", req.Message, comment.PageId ?? "");

                        apiSuccess = result.Success;
                        apiError = result.Error;
                    }
                    else if (req.Platform == "instagram" || req.Platform == "instagram_dm")
                    {
                        var result = await SendInstagramCommentReplyAsync(comment.CommentId ?? "", req.Message, comment.PageId ?? "");
                        apiSuccess = result.Success;
                        apiError = result.Error;
                    }
                    else if (req.Platform == "twitter")
                    {
                        var result = await SendTwitterReplyAsync(comment.CommentId ?? "", req.Message);
                        apiSuccess = result.Success;
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
            await _hub.Clients.All.SendAsync("inboxChanged");

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
            var fields = "id,message,story,created_time,permalink_url," +
                 "attachments{media,title,description,url,type,subattachments}," +
                 "likes.summary(true),comments.summary(true),shares";

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

        // ── GetThread — full merged conversation for a DM sender ──────
        // Unlike GetReplies (which only shows one message + our replies to
        // THAT specific row), this pulls every PageMessage ever received from
        // this sender on this platform/page plus every reply we've sent back,
        // interleaved chronologically — a real chat history, not a per-item
        // ticket view. Only meaningful for messages; comments stay per-item.
        [HttpGet]
        [Route("Inbox/GetThread")]
        public async Task<IActionResult> GetThread(int itemId)
        {
            var anchor = await _db.PageMessages.FindAsync(itemId);
            if (anchor == null) return Json(new { success = false, message = "Not found" });

            var incoming = await _db.PageMessages
                .Where(m => m.SenderId == anchor.SenderId && m.Platform == anchor.Platform && m.PageId == anchor.PageId)
                .OrderBy(m => m.MessageTime ?? m.CreatedAt)
                .ToListAsync();

            var incomingIds = incoming.Select(m => m.Id).ToList();

            var outgoing = await _db.PageReplies
                .Where(r => r.SourceType == "message" && incomingIds.Contains(r.SourceId))
                .OrderBy(r => r.CreatedAt)
                .ToListAsync();

            var merged = new List<object>();
            foreach (var m in incoming)
            {
                merged.Add(new
                {
                    own = false,
                    sender = string.IsNullOrEmpty(m.SenderName) ? "User " + (m.SenderId ?? "").PadLeft(6, '0')[^6..] : m.SenderName,
                    text = m.MessageText ?? "",
                    time = (m.MessageTime ?? m.CreatedAt).ToString("o"),
                    failed = false,
                    error = (string?)null
                });
            }
            foreach (var r in outgoing)
            {
                merged.Add(new
                {
                    own = true,
                    sender = "You (Page)",
                    text = r.ReplyText,
                    time = r.CreatedAt.ToString("o"),
                    failed = !r.IsSuccess,
                    error = r.ErrorMessage
                });
            }

            var sorted = merged.OrderBy(x => ((dynamic)x).time).ToList();
            return Json(new { success = true, messages = sorted });
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
            // Native-login accounts (own token, no linked Facebook Page) take priority.
            var nativeToken = await GetNativeInstagramTokenAsync(pageId);
            if (!string.IsNullOrEmpty(nativeToken))
                return await _nativeInstagram.ReplyToCommentAsync(commentId, message, nativeToken);

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

        // ══ Twitter API helper ═══════════════════════════════════════
        // Replying to a mention on Twitter/X IS posting a new tweet, with
        // reply.in_reply_to_tweet_id set to the mention's tweet ID — there
        // is no separate "reply" endpoint like Facebook/Instagram comments.
        // Uses tweet.write, which is already part of this app's granted
        // Twitter scope (no reconnect needed, unlike DMs/likes/retweets).
        private async Task<(bool Success, string Error)> SendTwitterReplyAsync(string tweetId, string message)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            var account = await _db.TwitterAccounts
                .FirstOrDefaultAsync(t => t.UserId == userId && t.IsActive);
            if (account == null || string.IsNullOrEmpty(account.AccessToken))
                return (false, "Twitter not connected.");

            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", account.AccessToken);

            var payload = new { text = message, reply = new { in_reply_to_tweet_id = tweetId } };
            var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

            var resp = await client.PostAsync("https://api.twitter.com/2/tweets", content);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return (false, $"Twitter API error: {json}");

            return (true, "");
        }

        private async Task<(bool Success, string Error)> SendInstagramDMAsync(
            string recipientId, string message, string pageId)
        {
            var nativeToken = await GetNativeInstagramTokenAsync(pageId);
            if (!string.IsNullOrEmpty(nativeToken))
                return await _nativeInstagram.SendDMAsync(recipientId, message, nativeToken);

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
        // Resolves the token for the SPECIFIC page/account that owns this
        // comment/message (by pageId), not just "any" connected page — matters
        // as soon as a user has more than one Page connected.
        private async Task<string?> GetPageAccessTokenAsync(string pageId, string platform)
        {
            var userid = HttpContext.Session.GetInt32("UserId");
            if (userid == null) return null;

            if (platform == "instagram" || platform == "instagram_dm")
            {
                var linkedPage = await _activePages.GetLinkedPageForInstagramAsync(userid.Value, pageId);
                if (linkedPage != null) return linkedPage.page_access_token;
            }

            var pages = await _activePages.GetAllFacebookPagesAsync(userid.Value);
            var exact = pages.FirstOrDefault(p => p.page_id == pageId);
            if (exact != null) return exact.page_access_token;

            // Fallback: single-page users / unmatched id — use the active page
            var active = await _activePages.GetActiveFacebookPageAsync(userid.Value);
            return active?.page_access_token;
        }

        // ── Private: merge comments + messages into unified list ──────
        private async Task<List<InboxItem>> GetMergedItems()
        {
            var userId = HttpContext.Session.GetInt32("UserId");

            var pageIds = await _db.FacebookPages
                .Where(o => o.user_id == userId.ToString())
                .Select(o => o.page_id)
                .Distinct()
                .ToListAsync();

            var pageIdsig = await _db.InstagramAccounts
                .Where(m => m.UserId == userId.ToString())
                .Select(m => m.InstagramUserId)
                .ToListAsync();

            // Twitter mentions (Controllers/TwitterController.SyncMentions) are saved
            // into this same page_comments table keyed by the account's own Twitter
            // user ID as PageId — include it here so they show up in the merged feed.
            var twitterId = await _db.TwitterAccounts
                .Where(t => t.UserId == userId && t.IsActive)
                .Select(t => t.TwitterUserId)
                .FirstOrDefaultAsync();

            // Combine ALL page IDs into one list to avoid missing matches
            var allPageIds = pageIds.Concat(pageIdsig)
                .Concat(twitterId != null ? new[] { twitterId } : Array.Empty<string>())
                .Distinct().ToList();

            // ── Comments ──────────────────────────────────────────────────
            var comments = await _db.PageComments
                .Where(c => allPageIds.Contains(c.PageId))
                .OrderByDescending(c => c.CommentTime ?? c.CreatedAt)
                .Select(c => new InboxItem
                {
                    Id = c.Id,
                    ItemType = "comment",
                    Platform = c.Platform,
                    SenderId = c.SenderId ?? "",
                    SenderName = !string.IsNullOrEmpty(c.SenderName)
                                      ? c.SenderName
                                      : !string.IsNullOrEmpty(c.SenderId)
                                          ? "User " + c.SenderId.Substring(Math.Max(0, c.SenderId.Length - 6))
                                          : "Unknown",
                    Message = c.Message ?? "",
                    PostId = c.PostId ?? "",
                    PageId = c.PageId ?? "",
                    CommentId = c.CommentId ?? "",   // ← map CommentId
                    CommentType = c.CommentType ?? "comment",
                    IsReplied = false,
                    Time = c.CommentTime ?? c.CreatedAt
                })
                .ToListAsync();

            // ── Messages ──────────────────────────────────────────────────
            // After fetching messages, group by SenderId+Platform and keep only latest per conversation
            var messages = (await _db.PageMessages
                .Where(m => allPageIds.Contains(m.PageId))
                .OrderByDescending(m => m.MessageTime ?? m.CreatedAt)
                .ToListAsync())
                .GroupBy(m => new { m.SenderId, m.Platform, m.PageId })  // group by conversation
                .Select(g => g.First())  // keep only the latest message per conversation
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
                    CommentId = "",
                    CommentType = "message",
                    IsReplied = m.IsReplied,
                    Time = m.MessageTime ?? m.CreatedAt
                })
                .ToList();

            return comments
                .Concat(messages)
                .DistinctBy(x => new { x.Id, x.ItemType })  // ← prevent duplicates
                .OrderByDescending(x => x.Time)
                .ToList();
        }

        // After receiving a DM webhook, fetch the sender name
        private async Task<string> GetFacebookUserNameAsync(string senderId, string pageAccessToken)
        {
            try
            {
                var client = new HttpClient();
                var url = $"https://graph.facebook.com/v19.0/{senderId}?fields=name&access_token={pageAccessToken}";
                var resp = await client.GetAsync(url);
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            }
            catch { return ""; }
        }

        // In your webhook controller, when saving a new message:
        private async Task EnrichAndSaveMessage(PageMessage msg, string pageAccessToken)
        {
            // Fetch sender name from Facebook/Instagram Graph API
            if (string.IsNullOrEmpty(msg.SenderName) && !string.IsNullOrEmpty(msg.SenderId))
            {
                msg.SenderName = await GetSenderNameAsync(msg.SenderId, pageAccessToken, msg.Platform);
            }

            _db.PageMessages.Add(msg);
            await _db.SaveChangesAsync();
        }



        private async Task<string> GetSenderNameAsync(string senderId, string accessToken, string platform)
        {
            try
            {
                var client = new HttpClient();
                string url;

                if (platform == "instagram" || platform == "instagram_dm")
                {
                    // Instagram: fetch username
                    url = $"https://graph.facebook.com/v19.0/{senderId}?fields=name,username&access_token={accessToken}";
                }
                else
                {
                    // Facebook: fetch name
                    url = $"https://graph.facebook.com/v19.0/{senderId}?fields=name&access_token={accessToken}";
                }

                var resp = await client.GetAsync(url);
                var json = await resp.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Try name first, then username
                if (root.TryGetProperty("name", out var name) && !string.IsNullOrEmpty(name.GetString()))
                    return name.GetString()!;

                if (root.TryGetProperty("username", out var uname) && !string.IsNullOrEmpty(uname.GetString()))
                    return "@" + uname.GetString();

                return "";
            }
            catch { return ""; }
        }
        [HttpGet]
        [Route("Inbox/BackfillNames")]
        public async Task<IActionResult> BackfillNames()
        {
            var userId = HttpContext.Session.GetInt32("UserId");

            // Get page token
            var page = await _db.FacebookPages
                .FirstOrDefaultAsync(p => p.user_id == userId.ToString());

            if (page == null) return Json(new { error = "No page found" });

            var token = page.page_access_token;

            // Find all messages with missing sender names
            var messages = await _db.PageMessages
                .Where(m => string.IsNullOrEmpty(m.SenderName) && !string.IsNullOrEmpty(m.SenderId))
                .ToListAsync();

            var client = new HttpClient();
            int updated = 0;

            foreach (var msg in messages)
            {
                try
                {
                    var url = $"https://graph.facebook.com/v19.0/{msg.SenderId}?fields=name,username&access_token={token}";
                    var resp = await client.GetAsync(url);
                    var json = await resp.Content.ReadAsStringAsync();

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string name = "";
                    if (root.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
                    else if (root.TryGetProperty("username", out var u)) name = "@" + u.GetString();

                    if (!string.IsNullOrEmpty(name))
                    {
                        msg.SenderName = name;
                        updated++;
                    }
                }
                catch { /* skip this one */ }

                await Task.Delay(50); // avoid rate limit
            }

            await _db.SaveChangesAsync();
            return Json(new { success = true, updated });
        }
    }

    // ── Unified inbox item (used for both comments + messages) ────────
    public class InboxItem
    {
        public int Id { get; set; }
        public string ItemType { get; set; } = "comment";
        public string Platform { get; set; } = "";
        public string SenderId { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string Message { get; set; } = "";
        public string PostId { get; set; } = "";
        public string PageId { get; set; } = "";
        public string PostName { get; set; } = "";
        public string CommentId { get; set; } = "";   // ← ADD THIS
        public string CommentType { get; set; } = "comment";
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