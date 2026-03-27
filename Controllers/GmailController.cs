using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.AspNetCore.Mvc;
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

        public GmailController(
            AppDbContext db,
            GmailService gmail,
            GmailIntegrationService gmailIntegration)
        {
            _db = db;
            _gmail = gmail;
            _gmailIntegration = gmailIntegration;
        }

        // ── Helper: get current user ID ──────────────────────────────

        // ── Helper: get current user ID ──────────────────────────────
        private string UserId
        {
            get
            {
                var userId = HttpContext.Session.GetInt32("UserId");

                if (userId == null)
                    throw new Exception("Session expired");

                return userId.Value.ToString();
            }
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
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                    return Json(new { success = false, message = ex.Message });

                ViewBag.Error = "Failed to load inbox: " + ex.Message;
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
        // ══════════════════════════════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Disconnect()
        {
            try
            {
                await _gmailIntegration.DisconnectAsync(UserId);
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