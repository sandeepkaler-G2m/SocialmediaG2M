using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Hubs;
using SocialMediaPanel.Models;
using SocialMediaPanel.Services;

namespace SocialMediaPanel.Controllers
{
    /// <summary>
    /// Real WhatsApp Business Cloud API integration. Self-service — the user
    /// pastes their own Phone Number ID + access token (generated in Meta
    /// Business Manager) instead of an OAuth flow, since WhatsApp Business
    /// API has no per-user consent screen the way Facebook/Instagram/
    /// Twitter/LinkedIn do.
    ///
    /// Deliberately has its own dedicated conversation view (contact list +
    /// thread, like WhatsApp itself) rather than feeding into the shared
    /// Smart Inbox — WhatsApp is a distinct, often higher-volume channel
    /// that reads better as its own chat UI.
    ///
    /// Note on PageReply.PageId for this platform only: every other
    /// platform stores PageId = "my own account/page id" on a reply, since
    /// the reply is always anchored to one specific incoming item. WhatsApp
    /// conversations need to support sending the FIRST message to a brand
    /// new contact with no prior incoming message to anchor to, so replies
    /// here store PageId = the CONTACT's WhatsApp number instead — that's
    /// what GetThread matches on to rebuild a conversation.
    /// </summary>
    public class WhatsAppController : Controller
    {
        private readonly AppDbContext _db;
        private readonly WhatsAppBusinessService _wa;
        private readonly IHubContext<InboxHub> _hub;

        public WhatsAppController(AppDbContext db, WhatsAppBusinessService wa, IHubContext<InboxHub> hub)
        {
            _db = db;
            _wa = wa;
            _hub = hub;
        }

        private int? UserId => HttpContext.Session.GetInt32("UserId");

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (UserId == null) return RedirectToAction("Login", "Account");

            var integration = await _db.WhatsAppIntegrations
                .FirstOrDefaultAsync(w => w.UserId == UserId && w.IsActive);

            ViewBag.Integration = integration;

            if (integration != null)
            {
                var conversations = await BuildConversationsAsync(integration.PhoneNumberId);
                ViewBag.Conversations = conversations;
                ViewBag.TotalContacts = conversations.Count;
                ViewBag.TotalReceived = await _db.PageMessages.CountAsync(m => m.PageId == integration.PhoneNumberId && m.Platform == "whatsapp");
                ViewBag.TotalSent = await _db.PageReplies.CountAsync(r => r.Platform == "whatsapp" && r.IsSuccess);
                ViewBag.TotalFailed = await _db.PageReplies.CountAsync(r => r.Platform == "whatsapp" && !r.IsSuccess);
            }

            return View();
        }

        // ── Conversation list — grouped by contact, latest message first ──
        private async Task<List<WhatsAppConversationItem>> BuildConversationsAsync(string phoneNumberId)
        {
            var incoming = await _db.PageMessages
                .Where(m => m.PageId == phoneNumberId && m.Platform == "whatsapp")
                .OrderByDescending(m => m.MessageTime ?? m.CreatedAt)
                .ToListAsync();

            var outgoing = await _db.PageReplies
                .Where(r => r.Platform == "whatsapp")
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();

            var contactIds = incoming.Select(m => m.SenderId!)
                .Concat(outgoing.Select(r => r.PageId))
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct();

            var list = new List<WhatsAppConversationItem>();
            foreach (var contactId in contactIds)
            {
                var lastIncoming = incoming.FirstOrDefault(m => m.SenderId == contactId);
                var lastOutgoing = outgoing.FirstOrDefault(r => r.PageId == contactId);

                DateTime lastTime; string lastText; bool lastWasOwn;
                var incomingTime = lastIncoming?.MessageTime ?? lastIncoming?.CreatedAt;
                var outgoingTime = lastOutgoing?.CreatedAt;

                if (outgoingTime.HasValue && (!incomingTime.HasValue || outgoingTime > incomingTime))
                {
                    lastTime = outgoingTime.Value; lastText = lastOutgoing!.ReplyText; lastWasOwn = true;
                }
                else
                {
                    lastTime = incomingTime ?? DateTime.UtcNow; lastText = lastIncoming?.MessageText ?? ""; lastWasOwn = false;
                }

                list.Add(new WhatsAppConversationItem
                {
                    ContactId = contactId,
                    ContactName = lastIncoming?.SenderName,
                    LastMessage = lastText,
                    LastMessageTime = lastTime,
                    LastMessageWasOwn = lastWasOwn,
                    UnreadCount = incoming.Count(m => m.SenderId == contactId && !m.IsReplied)
                });
            }

            return list.OrderByDescending(c => c.LastMessageTime).ToList();
        }

        [HttpGet]
        [Route("WhatsApp/GetConversations")]
        public async Task<IActionResult> GetConversations()
        {
            if (UserId == null) return Unauthorized();
            var integration = await _db.WhatsAppIntegrations.FirstOrDefaultAsync(w => w.UserId == UserId && w.IsActive);
            if (integration == null) return Json(new { success = false, message = "Not connected" });

            var conversations = await BuildConversationsAsync(integration.PhoneNumberId);
            return Json(new { success = true, conversations });
        }

        // ── Full merged thread for one contact ────────────────────────────
        [HttpGet]
        [Route("WhatsApp/GetThread")]
        public async Task<IActionResult> GetThread(string contactId)
        {
            if (UserId == null) return Unauthorized();
            var integration = await _db.WhatsAppIntegrations.FirstOrDefaultAsync(w => w.UserId == UserId && w.IsActive);
            if (integration == null) return Json(new { success = false, message = "Not connected" });

            var incoming = await _db.PageMessages
                .Where(m => m.PageId == integration.PhoneNumberId && m.Platform == "whatsapp" && m.SenderId == contactId)
                .OrderBy(m => m.MessageTime ?? m.CreatedAt)
                .ToListAsync();

            var outgoing = await _db.PageReplies
                .Where(r => r.Platform == "whatsapp" && r.PageId == contactId)
                .OrderBy(r => r.CreatedAt)
                .ToListAsync();

            var merged = new List<object>();
            foreach (var m in incoming)
                merged.Add(new { own = false, text = m.MessageText ?? "", time = (m.MessageTime ?? m.CreatedAt).ToString("o"), failed = false, error = (string?)null });
            foreach (var r in outgoing)
                merged.Add(new { own = true, text = r.ReplyText, time = r.CreatedAt.ToString("o"), failed = !r.IsSuccess, error = r.ErrorMessage });

            var sorted = merged.OrderBy(x => ((dynamic)x).time).ToList();

            // Mark this contact's incoming messages as "replied"/seen now that the thread's been opened.
            if (incoming.Any(m => !m.IsReplied))
            {
                foreach (var m in incoming.Where(m => !m.IsReplied)) m.IsReplied = true;
                await _db.SaveChangesAsync();
            }

            return Json(new { success = true, contactName = incoming.LastOrDefault(m => !string.IsNullOrEmpty(m.SenderName))?.SenderName, messages = sorted });
        }

        // ── Save credentials (connect) ──────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> Connect(string phoneNumberId, string accessToken, string? wabaId)
        {
            if (UserId == null) return RedirectToAction("Login", "Account");

            if (string.IsNullOrWhiteSpace(phoneNumberId) || string.IsNullOrWhiteSpace(accessToken))
            {
                TempData["WhatsAppError"] = "Phone Number ID and Access Token are both required.";
                return RedirectToAction("Index");
            }

            var (success, displayNumber, verifiedName, error) =
                await _wa.GetPhoneNumberInfoAsync(phoneNumberId.Trim(), accessToken.Trim());

            if (!success)
            {
                TempData["WhatsAppError"] = "Could not verify these credentials with Meta: " + error;
                return RedirectToAction("Index");
            }

            var existing = await _db.WhatsAppIntegrations
                .FirstOrDefaultAsync(w => w.UserId == UserId);

            if (existing != null)
            {
                existing.PhoneNumberId = phoneNumberId.Trim();
                existing.WabaId = wabaId?.Trim();
                existing.AccessToken = accessToken.Trim();
                existing.DisplayPhoneNumber = displayNumber;
                existing.VerifiedName = verifiedName;
                existing.IsActive = true;
            }
            else
            {
                _db.WhatsAppIntegrations.Add(new WhatsAppIntegration
                {
                    UserId = UserId!.Value,
                    PhoneNumberId = phoneNumberId.Trim(),
                    WabaId = wabaId?.Trim(),
                    AccessToken = accessToken.Trim(),
                    DisplayPhoneNumber = displayNumber,
                    VerifiedName = verifiedName,
                    IsActive = true,
                    ConnectedAt = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync();
            TempData["WhatsAppSuccess"] = $"Connected {displayNumber ?? phoneNumberId} ({verifiedName ?? "verified"}).";
            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> Disconnect()
        {
            if (UserId == null) return RedirectToAction("Login", "Account");

            var integration = await _db.WhatsAppIntegrations.FirstOrDefaultAsync(w => w.UserId == UserId);
            if (integration != null)
            {
                integration.IsActive = false;
                await _db.SaveChangesAsync();
            }
            TempData["WhatsAppSuccess"] = "WhatsApp disconnected.";
            return RedirectToAction("Index");
        }

        // ── Send a message to a contact ───────────────────────────────────
        [HttpPost]
        [Route("WhatsApp/SendMessage")]
        public async Task<IActionResult> SendMessage(string to, string message)
        {
            if (UserId == null) return Json(new { success = false, message = "Not logged in" });
            if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(message))
                return Json(new { success = false, message = "Recipient and message are required." });

            var integration = await _db.WhatsAppIntegrations
                .FirstOrDefaultAsync(w => w.UserId == UserId && w.IsActive);
            if (integration == null)
                return Json(new { success = false, message = "WhatsApp not connected." });

            var to_ = to.Trim();
            var (success, messageId, error) = await _wa.SendTextMessageAsync(
                integration.PhoneNumberId, integration.AccessToken, to_, message);

            _db.PageReplies.Add(new PageReply
            {
                SourceId = 0,
                SourceType = "message",
                Platform = "whatsapp",
                PageId = to_, // the CONTACT's number — see class-level note
                PostId = "",
                ReplyText = message,
                SentByPageId = integration.PhoneNumberId,
                IsSuccess = success,
                ErrorMessage = success ? null : error,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
            await _hub.Clients.All.SendAsync("whatsappChanged");

            if (!success)
            {
                var friendly = error.Contains("customer service window", StringComparison.OrdinalIgnoreCase) ||
                               error.Contains("#131047")
                    ? "This contact hasn't messaged you in the last 24 hours — WhatsApp requires an approved template message to start a new conversation."
                    : error;
                return Json(new { success = false, message = friendly });
            }

            return Json(new { success = true, messageId });
        }
    }

    public class WhatsAppConversationItem
    {
        public string ContactId { get; set; } = "";
        public string? ContactName { get; set; }
        public string LastMessage { get; set; } = "";
        public DateTime LastMessageTime { get; set; }
        public bool LastMessageWasOwn { get; set; }
        public int UnreadCount { get; set; }
    }
}
