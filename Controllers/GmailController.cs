using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using SocialMediaPanel.Services;
using System.Text.Json;

namespace SocialMediaPanel.Controllers
{
    /// <summary>
    /// Dedicated controller for all Gmail operations.
    /// Completely separate from IntegrationsController.
    ///
    /// Routes:
    ///   GET  /Gmail/Inbox                → list emails
    ///   GET  /Gmail/Read/{messageId}     → read one email
    ///   GET  /Gmail/Compose              → compose form
    ///   POST /Gmail/Compose              → send email
    ///   POST /Gmail/Reply                → reply to thread
    ///   GET  /Gmail/Labels               → list labels (JSON)
    ///   GET  /Gmail/Search?q=...         → search emails (JSON)
    ///   GET  /Gmail/UnreadCount          → badge count (JSON)
    ///   GET  /Gmail/Status               → is connected? (JSON)
    ///   POST /Gmail/Disconnect           → revoke + delete
    /// </summary>
    public class GmailController : Controller
    {
        private readonly AppDbContext _db;
        private readonly GmailService _gmail;
        private readonly GmailIntegrationService _gmailIntegration;
        private readonly AuditLogService _audit;

        public GmailController(
            AppDbContext db,
            GmailService gmail,
            GmailIntegrationService gmailIntegration,
            AuditLogService audit)
        {
            _db = db;
            _gmail = gmail;
            _gmailIntegration = gmailIntegration;
            _audit = audit;
        }

        // ── Helper: get current user ID ──────────────────────────────
        // Every action below calls this as a plain argument (e.g.
        // _gmailIntegration.GetAsync(UserId)) OUTSIDE any try/catch, so
        // throwing here 500'd every Gmail endpoint whenever the session had
        // expired or the user was never logged in. Returning "" instead lets
        // GmailIntegrationService.GetAsync("") fall through to its normal
        // "integration == null" branch, which every caller already handles.
        private string UserId
        {
            get
            {
                var userId = HttpContext.Session.GetInt32("UserId");
                return userId?.ToString() ?? "";
            }
        }

        // ── Belt-and-braces: block every action except NotConnected when
        // there's no session, instead of leaning only on GmailIntegrationService
        // treating "" as "no match" (see the security note on GetAsync).
        // Page-rendering GETs (Inbox/Compose without ajaxOnly) redirect to
        // Login; everything else — including AJAX Inbox — gets a 401 JSON.
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var action = context.ActionDescriptor.RouteValues["action"] ?? "";
            var isNotConnected = string.Equals(action, "NotConnected", StringComparison.OrdinalIgnoreCase);

            if (!isNotConnected && HttpContext.Session.GetInt32("UserId") == null)
            {
                var isPageRender =
                    (string.Equals(action, "Inbox", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(action, "Compose", StringComparison.OrdinalIgnoreCase)) &&
                    Request.Method == HttpMethods.Get &&
                    Request.Headers["X-Requested-With"] != "XMLHttpRequest";

                context.Result = isPageRender
                    ? new RedirectToActionResult("Login", "Account", null)
                    : new UnauthorizedObjectResult(new { success = false, message = "Not logged in" });
            }

            base.OnActionExecuting(context);
        }

        // ══════════════════════════════════════════════════════════════
        // INBOX — list emails
        // GET /Gmail/Inbox?q=is:unread&max=20
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Inbox(
            string q = "in:inbox",
            int max = 20,
            bool ajaxOnly = false)
        {
            var integration = await _gmailIntegration.GetAsync(UserId);
            if (integration == null)
                return RedirectToAction("NotConnected");

            try
            {
                var token = await _gmailIntegration.EnsureFreshTokenAsync(integration!);
                var emails = await _gmail.GetEmailsAsync(token, max, q);
                var unread = emails.Count(e => e.IsUnread);
                var labels = await _gmail.GetLabelsAsync(token);

                if (ajaxOnly || Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                    return Json(new { success = true, emails, unread, total = emails.Count });

                ViewBag.Emails = emails;
                ViewBag.Unread = unread;
                ViewBag.Labels = labels.Where(l => l.Type == "user").ToList();
                ViewBag.Integration = integration;
                ViewBag.Query = q;
                return View();
            }
            catch (Exception ex)
            {
                // "invalid_grant" on a token REFRESH (not the initial code
                // exchange) means Google has permanently rejected this
                // stored refresh token — most commonly because the Google
                // Cloud OAuth consent screen is still in "Testing" status,
                // where every refresh token expires after 7 days regardless
                // of use (the fix is publishing the app in Google Cloud
                // Console), or because the user revoked access on Google's
                // side. Either way the stored token is dead, not just
                // stale, so mark this integration inactive and point the
                // user at reconnecting instead of showing raw Google JSON.
                var isDeadGrant = ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
                if (isDeadGrant)
                {
                    integration.IsActive = false;
                    await _db.SaveChangesAsync();
                }

                var friendlyMessage = isDeadGrant
                    ? "Your Gmail connection has expired and needs to be reconnected."
                    : "Failed to load inbox: " + ex.Message;

                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                    return Json(new { success = false, message = friendlyMessage, needsReconnect = isDeadGrant });

                ViewBag.Error = friendlyMessage;
                ViewBag.NeedsReconnect = isDeadGrant;
                return View();
            }
        }

        // ══════════════════════════════════════════════════════════════
        // READ — get full email body
        // GET /Gmail/Read/{messageId}
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        [Route("Gmail/Read/{messageId}")]
        public async Task<IActionResult> Read(string messageId)
        {
            var integration = await _gmailIntegration.GetAsync(UserId);
            if (integration == null)
                return Json(new { success = false, message = "Gmail not connected." });

            try
            {
                var token = await _gmailIntegration.EnsureFreshTokenAsync(integration!);

                // Use Google.Apis to get full message (body + headers in one call)
                var credential = GoogleCredential.FromAccessToken(token);
                var gmailSvc = new Google.Apis.Gmail.v1.GmailService(
                    new Google.Apis.Services.BaseClientService.Initializer
                    {
                        HttpClientInitializer = credential,
                        ApplicationName = "SocialMediaPanel"
                    });

                var req = gmailSvc.Users.Messages.Get("me", messageId);
                req.Format = Google.Apis.Gmail.v1.UsersResource.MessagesResource
                                 .GetRequest.FormatEnum.Full;

                var msg = await req.ExecuteAsync();
                if (msg == null)
                    return Json(new { success = false, message = "Message not found." });

                // Extract headers
                string GetHdr(string name) =>
                    msg.Payload?.Headers?
                       .FirstOrDefault(h => string.Equals(h.Name, name,
                           StringComparison.OrdinalIgnoreCase))
                       ?.Value ?? "";

                // Extract HTML body
                var body = _gmail.ExtractBodyFromMessage(msg);

                return Json(new
                {
                    success = true,
                    body = body,
                    messageId,
                    from = GetHdr("From"),
                    to = GetHdr("To"),
                    subject = GetHdr("Subject"),
                    date = GetHdr("Date")
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Failed to load email: " + ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════════════
        // COMPOSE — GET shows form, POST sends email
        // GET  /Gmail/Compose
        // POST /Gmail/Compose
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        public IActionResult Compose()
        {
            return View();
        }

        [HttpPost]
        [Route("Gmail/Compose")]
        public async Task<IActionResult> Compose([FromForm] ComposeEmailRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.To) || string.IsNullOrWhiteSpace(req.Subject))
                return Json(new { success = false, message = "To and Subject are required." });

            var integration = await _gmailIntegration.GetAsync(UserId);
            if (integration == null)
                return Json(new { success = false, message = "Gmail not connected." });

            try
            {
                var token = await _gmailIntegration.EnsureFreshTokenAsync(integration!);
                var msgId = await _gmail.SendEmailAsync(
                    token,
                    req.To,
                    req.Subject,
                    req.BodyHtml ?? req.BodyText ?? ""
                );
                return Json(new { success = true, messageId = msgId, message = $"Email sent to {req.To}" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Send failed: " + ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════════════
        // REPLY — reply to a thread
        // POST /Gmail/Reply
        // ══════════════════════════════════════════════════════════════
        [HttpPost]
        [Route("Gmail/Reply")]
        public async Task<IActionResult> Reply([FromBody] ReplyEmailRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.MessageId) || string.IsNullOrWhiteSpace(req.ReplyBody))
                return Json(new { success = false, message = "Missing required fields." });

            var integration = await _gmailIntegration.GetAsync(UserId);
            if (integration == null)
                return Json(new { success = false, message = "Gmail not connected." });

            try
            {
                var token = await _gmailIntegration.EnsureFreshTokenAsync(integration!);
                var msgId = await _gmail.ReplyToEmailAsync(
                    token,
                    req.ThreadId,
                    req.MessageId,
                    req.To,
                    req.Subject,
                    req.ReplyBody
                );

                return Json(new { success = true, messageId = msgId });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Reply failed: " + ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════════════
        // LABELS — list user labels (JSON)
        // GET /Gmail/Labels
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Labels()
        {
            var integration = await _gmailIntegration.GetAsync(UserId);
            if (integration == null)
                return Json(new { success = false, message = "Gmail not connected." });

            try
            {
                var token = await _gmailIntegration.EnsureFreshTokenAsync(integration!);
                var labels = await _gmail.GetLabelsAsync(token);
                return Json(new { success = true, labels });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════════════
        // APPLY LABEL — label a message
        // POST /Gmail/ApplyLabel
        // ══════════════════════════════════════════════════════════════
        [HttpPost]
        public async Task<IActionResult> ApplyLabel([FromBody] ApplyLabelRequest req)
        {
            var integration = await _gmailIntegration.GetAsync(UserId);
            if (integration == null)
                return Json(new { success = false, message = "Gmail not connected." });

            try
            {
                var token = await _gmailIntegration.EnsureFreshTokenAsync(integration!);
                await _gmail.ApplyLabelAsync(token, req.MessageId, req.LabelId);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════════════
        // SEARCH — search emails by Gmail query
        // GET /Gmail/Search?q=from:noreply@meta.com&max=10
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Search(string q = "", int max = 20)
        {
            if (string.IsNullOrWhiteSpace(q))
                return Json(new { success = false, message = "Query required." });

            var integration = await _gmailIntegration.GetAsync(UserId);
            if (integration == null)
                return Json(new { success = false, message = "Gmail not connected." });

            try
            {
                var token = await _gmailIntegration.EnsureFreshTokenAsync(integration!);
                var results = await _gmail.SearchEmailsAsync(token, q, max);
                return Json(new { success = true, results, count = results.Count, query = q });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════════════
        // UNREAD COUNT — badge number for Smart Inbox / Dashboard
        // GET /Gmail/UnreadCount
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> UnreadCount()
        {
            var integration = await _gmailIntegration.GetAsync(UserId);
            if (integration == null)
                return Json(new { success = false, count = 0 });

            try
            {
                var token = await _gmailIntegration.EnsureFreshTokenAsync(integration!);
                var count = await _gmail.GetUnreadCountAsync(token);
                return Json(new { success = true, count });
            }
            catch
            {
                return Json(new { success = false, count = 0 });
            }
        }

        // ══════════════════════════════════════════════════════════════
        // STATUS — is Gmail connected for current user?
        // GET /Gmail/Status
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Status()
        {
            var integration = await _gmailIntegration.GetAsync(UserId);
            if (integration == null)
                return Json(new { connected = false });

            var expiringSoon = await _gmailIntegration.IsTokenExpiringSoonAsync(UserId);

            return Json(new
            {
                connected = true,
                email = integration!.EmailAddress,
                displayName = integration!.DisplayName,
                picture = integration!.ProfilePicture,
                connectedAt = integration!.ConnectedAt,
                expiringSoon
            });
        }

        // ══════════════════════════════════════════════════════════════
        // DISCONNECT — revoke token + mark inactive
        // POST /Gmail/Disconnect
        // Note: no [ValidateAntiForgeryToken] — the Inbox page's JS calls this
        // via a plain fetch() with no body/token (and the view never renders
        // one), so the attribute would 400 every real disconnect click. This
        // matches the pattern used by the app's other session-gated JSON
        // endpoints (e.g. Leads/Delete).
        [HttpPost]
        public async Task<IActionResult> Disconnect()
        {
            try
            {
                await _gmailIntegration.DisconnectAsync(UserId);
                _audit.Log(HttpContext.Session.GetInt32("UserId"), "gmail.disconnect", "");
                return Json(new { success = true, message = "Gmail disconnected." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════════════
        // NOT CONNECTED — shown when Gmail isn't set up yet
        // GET /Gmail/NotConnected
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        public IActionResult NotConnected()
        {
            return View();
        }
    }

    // ── Request models ────────────────────────────────────────────────

    public class ComposeEmailRequest
    {
        [System.ComponentModel.DataAnnotations.Required]
        public string To { get; set; } = "";
        [System.ComponentModel.DataAnnotations.Required]
        public string Subject { get; set; } = "";
        public string? BodyHtml { get; set; }
        public string? BodyText { get; set; }
    }

    public class ReplyEmailRequest
    {
        public string ThreadId { get; set; } = "";
        public string MessageId { get; set; } = "";
        public string To { get; set; } = "";
        public string Subject { get; set; } = "";
        public string ReplyBody { get; set; } = "";
    }

    public class ApplyLabelRequest
    {
        public string MessageId { get; set; } = "";
        public string LabelId { get; set; } = "";
    }
}