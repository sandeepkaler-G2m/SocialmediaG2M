using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Hubs;
using SocialMediaPanel.Models;
using SocialMediaPanel.Services;
using System.Text.RegularExpressions;

namespace SocialMediaPanel.Controllers
{
    /// <summary>
    /// WhatsApp Business section — dashboard, inbox, and connection
    /// management, via G2M's own WhatsApp API layer
    /// (go2market.ai/api/clouds3/customsend — confirmed live). A user can
    /// connect multiple numbers; exactly one is "default" at a time and
    /// drives the dashboard/inbox unless a specific number is picked via
    /// the ?id= query param (used by the number switcher).
    ///
    /// Bulk campaigns live in WhatsAppCampaignController
    /// (/WhatsApp/Campaigns) — kept as its own controller for the
    /// background send loop, but reachable only from this section's own
    /// sub-nav, not a top-level app nav tab.
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
        private readonly IDataProtector _protector;

        public WhatsAppController(AppDbContext db, WhatsAppBusinessService wa, IHubContext<InboxHub> hub, IConfiguration config, IDataProtectionProvider dataProtection)
        {
            _db = db;
            _wa = wa;
            _hub = hub;
            _config = config;
            _protector = dataProtection.CreateProtector("WhatsAppCredentials.Password.v1");
        }

        private int? UserId => HttpContext.Session.GetInt32("UserId");

        // Picks which connected number is "current" for this request: an
        // explicit ?id= wins (must belong to this user), else the marked
        // default, else just the first active one.
        private async Task<WhatsAppIntegration?> ResolveIntegrationAsync(int? id)
        {
            if (id.HasValue)
            {
                var picked = await _db.WhatsAppIntegrations.FirstOrDefaultAsync(w => w.Id == id && w.UserId == UserId && w.IsActive);
                if (picked != null) return picked;
            }

            return await _db.WhatsAppIntegrations.Where(w => w.UserId == UserId && w.IsActive)
                .OrderByDescending(w => w.IsDefault).ThenBy(w => w.Id)
                .FirstOrDefaultAsync();
        }

        // ══════════════════════════════════════════════════════════════
        // DASHBOARD
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Index(int? id)
        {
            if (UserId == null) return RedirectToAction("Login", "Account");

            var allIntegrations = await _db.WhatsAppIntegrations
                .Where(w => w.UserId == UserId && w.IsActive)
                .OrderByDescending(w => w.IsDefault).ThenBy(w => w.Id)
                .ToListAsync();

            ViewBag.Integrations = allIntegrations;

            var integration = id.HasValue
                ? allIntegrations.FirstOrDefault(w => w.Id == id)
                : allIntegrations.FirstOrDefault(w => w.IsDefault) ?? allIntegrations.FirstOrDefault();

            ViewBag.Integration = integration;

            if (integration == null) return View();

            var phoneId = integration.PhoneNumberId;

            ViewBag.TotalOutgoing = await _db.PageReplies.CountAsync(r => r.Platform == "whatsapp" && r.SentByPageId == phoneId && r.IsSuccess);
            ViewBag.TotalDelivered = await _db.PageReplies.CountAsync(r => r.Platform == "whatsapp" && r.SentByPageId == phoneId && (r.DeliveryStatus == "delivered" || r.DeliveryStatus == "read"));
            ViewBag.TotalIncoming = await _db.PageMessages.CountAsync(m => m.Platform == "whatsapp" && m.PageId == phoneId);
            ViewBag.ReplyCount = await _db.PageMessages.CountAsync(m => m.Platform == "whatsapp" && m.PageId == phoneId && m.IsReplied);

            var since = DateTime.UtcNow.Date.AddDays(-6);
            var incomingRows = await _db.PageMessages
                .Where(m => m.Platform == "whatsapp" && m.PageId == phoneId && (m.MessageTime ?? m.CreatedAt) >= since)
                .Select(m => (m.MessageTime ?? m.CreatedAt).Date)
                .ToListAsync();
            var outgoingRows = await _db.PageReplies
                .Where(r => r.Platform == "whatsapp" && r.SentByPageId == phoneId && r.CreatedAt >= since)
                .Select(r => r.CreatedAt.Date)
                .ToListAsync();

            var days = Enumerable.Range(0, 7).Select(o => since.AddDays(o)).ToList();
            ViewBag.TrendLabels = days.Select(d => d.ToString("dd MMM")).ToList();
            ViewBag.TrendIncoming = days.Select(d => incomingRows.Count(x => x == d)).ToList();
            ViewBag.TrendOutgoing = days.Select(d => outgoingRows.Count(x => x == d)).ToList();

            var recentCampaigns = await _db.WhatsAppCampaigns
                .Where(c => c.UserId == UserId && c.CreatedAt >= since)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
            ViewBag.RecentCampaigns = recentCampaigns;

            ViewBag.WaLink = $"https://wa.me/{(integration.DisplayPhoneNumber ?? integration.PhoneNumberId).TrimStart('+')}";

            return View();
        }

        [HttpPost]
        public async Task<IActionResult> SetDefault(int id)
        {
            if (UserId == null) return RedirectToAction("Login", "Account");

            var integrations = await _db.WhatsAppIntegrations.Where(w => w.UserId == UserId && w.IsActive).ToListAsync();
            foreach (var i in integrations) i.IsDefault = (i.Id == id);
            await _db.SaveChangesAsync();

            return RedirectToAction("Index", new { id });
        }

        // ══════════════════════════════════════════════════════════════
        // INBOX — moved off the dashboard into its own sub-page
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Inbox(int? id)
        {
            if (UserId == null) return RedirectToAction("Login", "Account");

            var integration = await ResolveIntegrationAsync(id);
            ViewBag.Integration = integration;
            ViewBag.Integrations = await _db.WhatsAppIntegrations.Where(w => w.UserId == UserId && w.IsActive).OrderByDescending(w => w.IsDefault).ToListAsync();

            if (integration != null)
                ViewBag.Conversations = await BuildConversationsAsync(integration.PhoneNumberId);

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
                .Where(r => r.Platform == "whatsapp" && r.SentByPageId == phoneNumberId)
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
        public async Task<IActionResult> GetConversations(int? id)
        {
            if (UserId == null) return Unauthorized();
            var integration = await ResolveIntegrationAsync(id);
            if (integration == null) return Json(new { success = false, message = "Not connected" });

            var conversations = await BuildConversationsAsync(integration.PhoneNumberId);
            return Json(new { success = true, conversations });
        }

        // ── Full merged thread for one contact ────────────────────────────
        [HttpGet]
        [Route("WhatsApp/GetThread")]
        public async Task<IActionResult> GetThread(string contactId, int? id)
        {
            if (UserId == null) return Unauthorized();
            var integration = await ResolveIntegrationAsync(id);
            if (integration == null) return Json(new { success = false, message = "Not connected" });

            var incoming = await _db.PageMessages
                .Where(m => m.PageId == integration.PhoneNumberId && m.Platform == "whatsapp" && m.SenderId == contactId)
                .OrderBy(m => m.MessageTime ?? m.CreatedAt)
                .ToListAsync();

            var outgoing = await _db.PageReplies
                .Where(r => r.Platform == "whatsapp" && r.SentByPageId == integration.PhoneNumberId && r.PageId == contactId)
                .OrderBy(r => r.CreatedAt)
                .ToListAsync();

            var merged = new List<object>();
            foreach (var m in incoming)
                merged.Add(new { own = false, text = m.MessageText ?? "", time = (m.MessageTime ?? m.CreatedAt).ToString("o"), failed = false, error = (string?)null, status = (string?)null });
            foreach (var r in outgoing)
                merged.Add(new { own = true, text = r.ReplyText, time = r.CreatedAt.ToString("o"), failed = !r.IsSuccess, error = r.ErrorMessage, status = r.DeliveryStatus });

            var sorted = merged.OrderBy(x => ((dynamic)x).time).ToList();

            if (incoming.Any(m => !m.IsReplied))
            {
                foreach (var m in incoming.Where(m => !m.IsReplied)) m.IsReplied = true;
                await _db.SaveChangesAsync();
            }

            return Json(new { success = true, contactName = incoming.LastOrDefault(m => !string.IsNullOrEmpty(m.SenderName))?.SenderName, messages = sorted });
        }

        // ══════════════════════════════════════════════════════════════
        // CONNECT — manual credential form, supports multiple numbers
        // ══════════════════════════════════════════════════════════════
        [HttpPost]
        public async Task<IActionResult> Connect(string phoneNumberId, string accessToken, string? wabaId, string? username, string? password, string? userId)
        {
            if (UserId == null) return RedirectToAction("Login", "Account");

            if (string.IsNullOrWhiteSpace(phoneNumberId) || string.IsNullOrWhiteSpace(accessToken))
            {
                TempData["WhatsAppError"] = "Phone Number and Access Token are both required.";
                return RedirectToAction("Index");
            }

            var (saved, verified, connectMessage) = await _wa.ConnectViaG2MApiAsync(
                phoneNumberId.Trim(), accessToken.Trim(), username?.Trim(), password, userId?.Trim());

            if (!saved)
            {
                TempData["WhatsAppError"] = connectMessage;
                return RedirectToAction("Index");
            }

            var encryptedPassword = string.IsNullOrEmpty(password) ? null : _protector.Protect(password);

            var hadAnyBefore = await _db.WhatsAppIntegrations.AnyAsync(w => w.UserId == UserId && w.IsActive);

            // Reconnecting the SAME number updates that row; a different
            // number becomes a brand-new row (multi-number support).
            var existing = await _db.WhatsAppIntegrations.FirstOrDefaultAsync(w => w.UserId == UserId && w.PhoneNumberId == phoneNumberId.Trim());
            WhatsAppIntegration integration;
            if (existing != null)
            {
                existing.WabaId = wabaId?.Trim();
                existing.AccessToken = accessToken.Trim();
                existing.DisplayPhoneNumber = phoneNumberId.Trim();
                existing.Username = username?.Trim();
                existing.PasswordEncrypted = encryptedPassword;
                existing.G2MUserId = userId?.Trim();
                existing.IsActive = true;
                integration = existing;
            }
            else
            {
                integration = new WhatsAppIntegration
                {
                    UserId = UserId!.Value,
                    PhoneNumberId = phoneNumberId.Trim(),
                    WabaId = wabaId?.Trim(),
                    AccessToken = accessToken.Trim(),
                    DisplayPhoneNumber = phoneNumberId.Trim(),
                    Username = username?.Trim(),
                    PasswordEncrypted = encryptedPassword,
                    G2MUserId = userId?.Trim(),
                    IsActive = true,
                    IsDefault = !hadAnyBefore, // first-ever number is default automatically
                    ConnectedAt = DateTime.UtcNow
                };
                _db.WhatsAppIntegrations.Add(integration);
            }

            await _db.SaveChangesAsync();
            TempData[verified ? "WhatsAppSuccess" : "WhatsAppWarning"] = connectMessage;
            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> Disconnect(int id)
        {
            if (UserId == null) return RedirectToAction("Login", "Account");

            var integration = await _db.WhatsAppIntegrations.FirstOrDefaultAsync(w => w.Id == id && w.UserId == UserId);
            if (integration != null)
            {
                integration.IsActive = false;
                integration.IsDefault = false;
                await _db.SaveChangesAsync();

                // Promote another connected number to default if one exists.
                var another = await _db.WhatsAppIntegrations.FirstOrDefaultAsync(w => w.UserId == UserId && w.IsActive);
                if (another != null) { another.IsDefault = true; await _db.SaveChangesAsync(); }
            }
            TempData["WhatsAppSuccess"] = "WhatsApp number disconnected.";
            return RedirectToAction("Settings");
        }

        // ══════════════════════════════════════════════════════════════
        // SETTINGS — manage all connected numbers
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Settings()
        {
            if (UserId == null) return RedirectToAction("Login", "Account");

            var integrations = await _db.WhatsAppIntegrations
                .Where(w => w.UserId == UserId && w.IsActive)
                .OrderByDescending(w => w.IsDefault).ThenBy(w => w.Id)
                .ToListAsync();

            return View(integrations);
        }

        // Password is stored encrypted (see Connect) — only decrypted here,
        // on demand, rather than ever being embedded in the Settings page's
        // initial HTML.
        [HttpGet]
        [Route("WhatsApp/RevealPassword/{id}")]
        public async Task<IActionResult> RevealPassword(int id)
        {
            if (UserId == null) return Json(new { success = false });
            var integration = await _db.WhatsAppIntegrations.FirstOrDefaultAsync(w => w.Id == id && w.UserId == UserId);
            if (integration == null) return Json(new { success = false, message = "Not found." });
            if (string.IsNullOrEmpty(integration.PasswordEncrypted)) return Json(new { success = false, message = "No password saved." });

            try
            {
                var password = _protector.Unprotect(integration.PasswordEncrypted);
                return Json(new { success = true, password });
            }
            catch
            {
                return Json(new { success = false, message = "Could not decrypt password." });
            }
        }

        // ══════════════════════════════════════════════════════════════
        // TEMPLATES — self-maintained list (no fetch API from G2M yet)
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Templates()
        {
            if (UserId == null) return RedirectToAction("Login", "Account");
            var templates = await _db.WhatsAppTemplates.Where(t => t.UserId == UserId).OrderBy(t => t.Name).ToListAsync();
            return View(templates);
        }

        // ── Rich template builder page ─────────────────────────────────
        [HttpGet]
        [Route("WhatsApp/Templates/Create")]
        public IActionResult TemplatesCreate()
        {
            if (UserId == null) return RedirectToAction("Login", "Account");
            return View("TemplateCreate");
        }

        // Validated against Meta's own real template rules (name pattern,
        // char limits, button counts) so the definition this saves maps
        // 1:1 onto Meta's real message_templates "components" shape —
        // wiring the actual Meta/G2M submission call later is a transform
        // of DefinitionJson, not a UI rewrite.
        [HttpPost]
        [Route("WhatsApp/Templates/Create")]
        public async Task<IActionResult> TemplatesCreate([FromBody] CreateTemplateRequest req)
        {
            if (UserId == null) return Json(new { success = false, message = "Not logged in" });

            if (string.IsNullOrWhiteSpace(req.Name) || !Regex.IsMatch(req.Name, "^[a-z0-9_]+$"))
                return Json(new { success = false, message = "Template name must be lowercase letters, numbers, and underscores only." });

            var category = (req.Category ?? "").ToUpper();
            if (category != "MARKETING" && category != "UTILITY" && category != "AUTHENTICATION")
                return Json(new { success = false, message = "Choose a category." });

            var def = req.Definition ?? new TemplateDefinition();
            if (string.IsNullOrWhiteSpace(def.BodyText))
                return Json(new { success = false, message = "Body text is required." });
            if (def.BodyText.Length > 1024)
                return Json(new { success = false, message = "Body text can't exceed 1024 characters." });
            if (!string.IsNullOrEmpty(def.FooterText) && def.FooterText.Length > 60)
                return Json(new { success = false, message = "Footer can't exceed 60 characters." });
            if (!string.IsNullOrEmpty(def.HeaderText) && def.HeaderText.Length > 60)
                return Json(new { success = false, message = "Header text can't exceed 60 characters." });

            var buttons = def.Buttons ?? new List<TemplateButton>();
            if (buttons.Count(b => b.Type == "QUICK_REPLY") > 3)
                return Json(new { success = false, message = "Maximum 3 Quick Reply buttons." });
            if (buttons.Count > 10)
                return Json(new { success = false, message = "Maximum 10 buttons total." });

            if (await _db.WhatsAppTemplates.AnyAsync(t => t.UserId == UserId && t.Name == req.Name.Trim()))
                return Json(new { success = false, message = "A template with this name already exists." });

            var template = new WhatsAppTemplate
            {
                UserId = UserId!.Value,
                Name = req.Name.Trim(),
                LanguageCode = string.IsNullOrWhiteSpace(req.LanguageCode) ? "en" : req.LanguageCode.Trim(),
                TemplateType = category,
                Description = req.Label?.Trim(),
                DefinitionJson = System.Text.Json.JsonSerializer.Serialize(def),
                Status = "pending",
                UsableAt = DateTime.UtcNow.AddHours(1),
                CreatedAt = DateTime.UtcNow
            };
            _db.WhatsAppTemplates.Add(template);
            await _db.SaveChangesAsync();

            return Json(new { success = true, message = $"Template \"{template.Name}\" submitted — usable in campaigns from {template.UsableAt:hh:mm tt} today.", templateId = template.Id });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteTemplate(int id)
        {
            if (UserId == null) return RedirectToAction("Login", "Account");
            var template = await _db.WhatsAppTemplates.FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId);
            if (template != null)
            {
                _db.WhatsAppTemplates.Remove(template);
                await _db.SaveChangesAsync();
            }
            return RedirectToAction("Templates");
        }

        // ── Send a message to a contact — text, media (image/video/audio/
        // document), or template — all via G2M's WhatsApp send API. ───────
        [HttpPost]
        [Route("WhatsApp/SendMessage")]
        public async Task<IActionResult> SendMessage(
            string to, string? message, int? id,
            string? mediaType, string? mediaUrl, string? caption, string? fileName,
            string? templateName, string? languageCode, string? templateParams)
        {
            if (UserId == null) return Json(new { success = false, message = "Not logged in" });
            if (string.IsNullOrWhiteSpace(to))
                return Json(new { success = false, message = "Recipient is required." });

            var hasMedia = !string.IsNullOrWhiteSpace(mediaType) && !string.IsNullOrWhiteSpace(mediaUrl);
            var hasTemplate = !string.IsNullOrWhiteSpace(templateName);
            if (!hasMedia && !hasTemplate && string.IsNullOrWhiteSpace(message))
                return Json(new { success = false, message = "Enter a message, attach media, or pick a template." });

            var integration = await ResolveIntegrationAsync(id);
            if (integration == null)
                return Json(new { success = false, message = "WhatsApp not connected." });

            var to_ = to.Trim();
            var apiKey = integration.AccessToken;

            bool success; string? messageId; string error; string replyText;

            if (hasTemplate)
            {
                var paramsList = string.IsNullOrWhiteSpace(templateParams)
                    ? null
                    : templateParams.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                (success, messageId, error) = await _wa.SendG2MTemplateAsync(
                    apiKey, to_, templateName!.Trim(), string.IsNullOrWhiteSpace(languageCode) ? "en" : languageCode.Trim(), paramsList);
                replyText = $"[template: {templateName}]" + (paramsList != null ? " (" + string.Join(", ", paramsList) + ")" : "");
            }
            else if (hasMedia)
            {
                (success, messageId, error) = await _wa.SendG2MMediaAsync(
                    apiKey, to_, mediaType!.Trim().ToLower(), mediaUrl!.Trim(), caption, fileName);
                replyText = $"[{mediaType}] {mediaUrl}" + (!string.IsNullOrWhiteSpace(caption) ? " — " + caption : "");
            }
            else
            {
                (success, messageId, error) = await _wa.SendG2MTextAsync(apiKey, to_, message!);
                replyText = message!;
            }

            _db.PageReplies.Add(new PageReply
            {
                SourceId = 0,
                SourceType = "message",
                Platform = "whatsapp",
                PageId = to_, // the CONTACT's number — see class-level note
                PostId = "",
                ReplyText = replyText,
                SentByPageId = integration.PhoneNumberId,
                IsSuccess = success,
                ErrorMessage = success ? null : error,
                WaMessageId = messageId,
                DeliveryStatus = success ? "sent" : null,
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

    public class CreateTemplateRequest
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "UTILITY";
        public string LanguageCode { get; set; } = "en";
        public string? Label { get; set; }
        public TemplateDefinition? Definition { get; set; }
    }

    // Shaped to map directly onto Meta's real message_templates
    // "components" array — see the class-level note on TemplatesCreate.
    public class TemplateDefinition
    {
        public string HeaderType { get; set; } = "NONE"; // NONE | TEXT | IMAGE | VIDEO | DOCUMENT | LOCATION
        public string? HeaderText { get; set; }
        public string? HeaderExample { get; set; }
        public string? HeaderMediaUrl { get; set; }
        public string BodyText { get; set; } = "";
        public List<string> BodyVariableExamples { get; set; } = new();
        public string? FooterText { get; set; }
        public List<TemplateButton> Buttons { get; set; } = new();
    }

    public class TemplateButton
    {
        public string Type { get; set; } = ""; // QUICK_REPLY | URL | PHONE_NUMBER | COPY_CODE | FLOW
        public string Text { get; set; } = "";
        public string? Url { get; set; }
        public string? UrlExample { get; set; }
        public string? PhoneNumber { get; set; }
        public string? CopyCodeExample { get; set; }
    }
}
