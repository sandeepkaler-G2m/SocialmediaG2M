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
    /// Real WhatsApp Business Cloud API integration, onboarded through
    /// Meta's WhatsApp Embedded Signup (G2M is the Tech Provider). Panel
    /// users just click "Connect with Facebook" — G2M's own Meta app
    /// handles the token exchange, phone number registration, and webhook
    /// subscription server-side, so the end-user never sees/pastes a
    /// Phone Number ID or access token.
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
        private readonly IConfiguration _config;

        public WhatsAppController(AppDbContext db, WhatsAppBusinessService wa, IHubContext<InboxHub> hub, IConfiguration config)
        {
            _db = db;
            _wa = wa;
            _hub = hub;
            _config = config;
        }

        private int? UserId => HttpContext.Session.GetInt32("UserId");

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (UserId == null) return RedirectToAction("Login", "Account");

            var integration = await _db.WhatsAppIntegrations
                .FirstOrDefaultAsync(w => w.UserId == UserId && w.IsActive);

            ViewBag.Integration = integration;
            ViewBag.WhatsAppAppId = _config["WhatsApp:AppId"] ?? "";
            ViewBag.WhatsAppConfigId = _config["WhatsApp:ConfigurationId"] ?? "";

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

        // ── Embedded Signup callback ────────────────────────────────────
        // The frontend runs FB.login with G2M's Configuration ID, gets a
        // short-lived code + wabaId + phoneNumberId back via postMessage
        // (30-second TTL), and POSTs them here immediately. This endpoint
        // does the three required server-to-server calls — token exchange,
        // phone number registration, webhook subscription — then verifies
        // and saves, all using G2M's own App ID/Secret, never anything the
        // end-user typed in.
        [HttpPost]
        [Route("WhatsApp/EmbeddedSignupCallback")]
        public async Task<IActionResult> EmbeddedSignupCallback(string code, string wabaId, string phoneNumberId)
        {
            if (UserId == null) return Json(new { success = false, message = "Not logged in" });

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(wabaId) || string.IsNullOrWhiteSpace(phoneNumberId))
                return Json(new { success = false, message = "Signup did not return a code, WABA ID, and phone number ID — please try connecting again." });

            var (exchanged, accessToken, exchangeError) = await _wa.ExchangeCodeForBusinessTokenAsync(code.Trim());
            if (!exchanged || string.IsNullOrEmpty(accessToken))
                return Json(new { success = false, message = "Could not exchange the signup code with Meta: " + exchangeError });

            var (registered, registerError) = await _wa.RegisterPhoneNumberAsync(phoneNumberId.Trim(), accessToken);
            if (!registered)
                return Json(new { success = false, message = "Phone number registration failed: " + registerError });

            var (subscribed, subscribeError) = await _wa.SubscribeToWebhooksAsync(wabaId.Trim(), accessToken);
            if (!subscribed)
                return Json(new { success = false, message = "Webhook subscription failed: " + subscribeError });

            var (verified, displayNumber, verifiedName, verifyError) =
                await _wa.GetPhoneNumberInfoAsync(phoneNumberId.Trim(), accessToken);
            if (!verified)
                return Json(new { success = false, message = "Connected, but could not fetch phone number details: " + verifyError });

            var existing = await _db.WhatsAppIntegrations.FirstOrDefaultAsync(w => w.UserId == UserId);
            if (existing != null)
            {
                existing.PhoneNumberId = phoneNumberId.Trim();
                existing.WabaId = wabaId.Trim();
                existing.AccessToken = accessToken;
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
                    WabaId = wabaId.Trim(),
                    AccessToken = accessToken,
                    DisplayPhoneNumber = displayNumber,
                    VerifiedName = verifiedName,
                    IsActive = true,
                    ConnectedAt = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync();
            return Json(new { success = true, message = $"Connected {displayNumber ?? phoneNumberId} ({verifiedName ?? "verified"})." });
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
