using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SocialMediaPanel.Controllers
{
    [Route("webhook")]
    [ApiController]
    public class WebhookController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _config;
        private readonly ILogger<WebhookController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public WebhookController(
            AppDbContext context,
            IConfiguration config,
            ILogger<WebhookController> logger,
            IHttpClientFactory httpClientFactory)
        {
            _context = context;
            _config = config;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        // ══════════════════════════════════════════════════════
        // GET /webhook
        // Meta webhook verification
        // Meta 3 params bhejta hai:
        //   hub.mode         = "subscribe"
        //   hub.verify_token = tumhara verify token (appsettings se)
        //   hub.challenge    = random string jo wapas bhejni hai
        // ══════════════════════════════════════════════════════
        [HttpGet]
        public IActionResult Verify(
            [FromQuery(Name = "hub.mode")] string? mode,
            [FromQuery(Name = "hub.challenge")] string? challenge,
            [FromQuery(Name = "hub.verify_token")] string? verifyToken)
        {
            var expectedToken = _config["Meta:WebhookVerifyToken"];

            _logger.LogInformation(
                "Webhook verify — mode={Mode} token={Token}", mode, verifyToken);

            if (mode == "subscribe" && verifyToken == expectedToken)
            {
                _logger.LogInformation("Webhook verified OK");
                return Content(challenge ?? "", "text/plain");
            }

            _logger.LogWarning("Webhook verification FAILED");
            return Forbid();
        }

        // ══════════════════════════════════════════════════════
        // POST /webhook
        // Meta real events yahan bhejta hai
        // FB Page + Instagram dono yahan aate hain
        // ══════════════════════════════════════════════════════
        [HttpPost]
        public async Task<IActionResult> Receive()
        {
            string rawBody;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                rawBody = await reader.ReadToEndAsync();
            }

            _logger.LogInformation("Webhook POST: {Body}",
                rawBody[..Math.Min(300, rawBody.Length)]);

            // Signature verify karo
            var sig = Request.Headers["X-Hub-Signature-256"].ToString();
            if (!VerifySignature(rawBody, sig))
            {
                _logger.LogWarning("Invalid signature — rejected");
                return Unauthorized("Invalid signature");
            }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(rawBody); }
            catch (JsonException ex)
            {
                _logger.LogError("JSON error: {Msg}", ex.Message);
                return BadRequest("Invalid JSON");
            }

            var root = doc.RootElement;
            var obj = root.TryGetProperty("object", out var op) ? op.GetString() : "";
            var hasEntry = root.TryGetProperty("entry", out var entries);

            // Raw event log karo
            await LogWebhookEvent(obj ?? "unknown", rawBody);

            if (hasEntry)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    switch (obj?.ToLower())
                    {
                        case "page": await HandlePageEvent(entry); break;
                        case "instagram": await HandleInstagramEvent(entry); break;
                        case "ad_account": await HandleAdAccountEvent(entry); break;
                        default:
                            _logger.LogInformation("Unknown object: {Obj}", obj);
                            break;
                    }
                }
            }

            // Meta ko hamesha 200 OK — warna retry karta hai
            return Ok("EVENT_RECEIVED");
        }

        // ══════════════════════════════════════════════════════
        // FACEBOOK PAGE EVENTS
        // ══════════════════════════════════════════════════════
        private async Task HandlePageEvent(JsonElement entry)
        {
            var pageId = entry.TryGetProperty("id", out var idP) ? idP.GetString() : null;

            // Direct Messenger messages
            if (entry.TryGetProperty("messaging", out var directMsg))
                await HandleMessaging(directMsg, pageId, "messenger");

            if (!entry.TryGetProperty("changes", out var changes)) return;

            foreach (var change in changes.EnumerateArray())
            {
                var field = change.TryGetProperty("field", out var f) ? f.GetString() : "";
                var value = change.TryGetProperty("value", out var v) ? v : (JsonElement?)null;

                _logger.LogInformation("Page field={Field} pageId={PageId}", field, pageId);

                switch (field?.ToLower())
                {
                    case "leadgen":
                        if (value.HasValue)
                            await FetchAndSaveLead(value.Value, pageId, "facebook");
                        break;

                    case "feed":
                        if (value.HasValue)
                            await HandleFeedChange(value.Value, pageId, "facebook");
                        break;

                    case "messages":
                        if (entry.TryGetProperty("messaging", out var msg))
                            await HandleMessaging(msg, pageId, "messenger");
                        break;

                    case "mention":
                        if (value.HasValue)
                            await SaveComment(value.Value, pageId, "facebook", "mention");
                        break;

                    case "ratings":
                        _logger.LogInformation("Rating received — pageId={PageId}", pageId);
                        break;
                }
            }
        }

        // ══════════════════════════════════════════════════════
        // INSTAGRAM EVENTS
        // ══════════════════════════════════════════════════════
        private async Task HandleInstagramEvent(JsonElement entry)
        {
            var igId = entry.TryGetProperty("id", out var idP) ? idP.GetString() : null;

            // Instagram DMs
            if (entry.TryGetProperty("messaging", out var messaging))
                await HandleMessaging(messaging, igId, "instagram_dm");

            if (!entry.TryGetProperty("changes", out var changes)) return;

            foreach (var change in changes.EnumerateArray())
            {
                var field = change.TryGetProperty("field", out var f) ? f.GetString() : "";
                var value = change.TryGetProperty("value", out var v) ? v : (JsonElement?)null;

                _logger.LogInformation("Instagram field={Field} igId={IgId}", field, igId);

                switch (field?.ToLower())
                {
                    case "comments":
                        if (value.HasValue)
                            await SaveComment(value.Value, igId, "instagram", "comment");
                        break;

                    case "mentions":
                        if (value.HasValue)
                            await SaveComment(value.Value, igId, "instagram", "mention");
                        break;

                    case "live_comments":
                        if (value.HasValue)
                            await SaveComment(value.Value, igId, "instagram", "live_comment");
                        break;

                    case "leadgen":
                        if (value.HasValue)
                            await FetchAndSaveLead(value.Value, igId, "instagram");
                        break;

                    case "story_insights":
                        _logger.LogInformation("Story insight — igId={IgId}", igId);
                        break;
                }
            }
        }

        // ══════════════════════════════════════════════════════
        // AD ACCOUNT EVENTS
        // ══════════════════════════════════════════════════════
        private async Task HandleAdAccountEvent(JsonElement entry)
        {
            var adId = entry.TryGetProperty("id", out var idP) ? idP.GetString() : null;

            if (!entry.TryGetProperty("changes", out var changes)) return;

            foreach (var change in changes.EnumerateArray())
            {
                var field = change.TryGetProperty("field", out var f) ? f.GetString() : "";
                var value = change.TryGetProperty("value", out var v) ? v : (JsonElement?)null;

                if (field == "lead_gen" && value.HasValue)
                    await FetchAndSaveLead(value.Value, adId, "facebook_ad");
            }
        }

        // ══════════════════════════════════════════════════════
        // FEED CHANGE
        // ══════════════════════════════════════════════════════
        private async Task HandleFeedChange(JsonElement value, string? pageId, string platform)
        {
            var item = value.TryGetProperty("item", out var i) ? i.GetString() : "";
            var verb = value.TryGetProperty("verb", out var vb) ? vb.GetString() : "";
            var postId = value.TryGetProperty("post_id", out var pid) ? pid.GetString() : null;

            _logger.LogInformation("Feed item={Item} verb={Verb}", item, verb);

            if (item == "comment" && verb == "add")
            {
                await SaveComment(value, pageId, platform, "comment");
                return;
            }

            if (postId != null)
            {
                if (item == "reaction" || item == "like")
                    await UpdatePostInsight(postId, pageId, platform, "like");
                else if (item == "share")
                    await UpdatePostInsight(postId, pageId, platform, "share");
            }
        }

        // ══════════════════════════════════════════════════════
        // MESSAGING — Messenger + Instagram DM
        // ══════════════════════════════════════════════════════
        private async Task HandleMessaging(JsonElement messaging, string? pageId, string platform)
        {
            foreach (var msgEvent in messaging.EnumerateArray())
            {
                if (!msgEvent.TryGetProperty("message", out var message)) continue;

                // Page ka apna echo message skip karo
                if (message.TryGetProperty("is_echo", out var echo) && echo.GetBoolean())
                    continue;

                var senderId = msgEvent.TryGetProperty("sender", out var s)
                    && s.TryGetProperty("id", out var sid) ? sid.GetString() : null;
                var messageId = message.TryGetProperty("mid", out var mid)
                    ? mid.GetString() : Guid.NewGuid().ToString();
                var text = message.TryGetProperty("text", out var txt)
                    ? txt.GetString() : null;
                var timestamp = msgEvent.TryGetProperty("timestamp", out var ts)
                    ? ts.GetInt64() : 0;

                // Duplicate check
                if (await _context.PageMessages.AnyAsync(m => m.MessageId == messageId))
                    continue;

                _context.PageMessages.Add(new PageMessage
                {
                    MessageId = messageId!,
                    PageId = pageId,
                    SenderId = senderId,
                    MessageText = text,
                    Platform = platform,
                    MessageTime = timestamp > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime
                        : DateTime.UtcNow,
                    IsReplied = false,
                    CreatedAt = DateTime.UtcNow
                });

                _logger.LogInformation(
                    "Message saved — platform={P} sender={S}", platform, senderId);
            }

            await _context.SaveChangesAsync();
        }

        // ══════════════════════════════════════════════════════
        // LEAD — webhook sirf ID deta hai
        // Graph API se poora data fetch karo
        // ══════════════════════════════════════════════════════
        private async Task FetchAndSaveLead(JsonElement value, string? pageId, string platform)
        {
            var leadId = value.TryGetProperty("leadgen_id", out var lid) ? lid.GetString()
                       : value.TryGetProperty("lead_id", out var lid2) ? lid2.GetString()
                       : null;
            var formId = value.TryGetProperty("form_id", out var fid) ? fid.GetString() : null;

            if (string.IsNullOrEmpty(leadId)) return;

            // Duplicate check
            if (await _context.Leads.AnyAsync(l => l.LeadId == leadId)) return;

            string? fullName = null, email = null, phone = null;
            string rawData = value.GetRawText();

            try
            {
                // DB se page access token lo
                var pageToken = await GetPageAccessToken(pageId);

                if (!string.IsNullOrEmpty(pageToken))
                {
                    var client = _httpClientFactory.CreateClient();
                    var url = $"https://graph.facebook.com/v19.0/{leadId}" +
                                 $"?fields=field_data,created_time,ad_id,form_id" +
                                 $"&access_token={pageToken}";

                    var response = await client.GetStringAsync(url);
                    rawData = response;

                    using var leadDoc = JsonDocument.Parse(response);
                    var leadRoot = leadDoc.RootElement;

                    // field_data parse karo
                    if (leadRoot.TryGetProperty("field_data", out var fieldData))
                    {
                        foreach (var field in fieldData.EnumerateArray())
                        {
                            var name = field.TryGetProperty("name", out var n)
                                ? n.GetString()?.ToLower() : "";
                            var vals = field.TryGetProperty("values", out var v2)
                                ? v2 : (JsonElement?)null;
                            var val = vals.HasValue && vals.Value.GetArrayLength() > 0
                                ? vals.Value[0].GetString() : null;

                            switch (name)
                            {
                                case "full_name":
                                case "name": fullName = val; break;
                                case "email":
                                case "email_address": email = val; break;
                                case "phone_number":
                                case "phone": phone = val; break;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(formId)
                        && leadRoot.TryGetProperty("form_id", out var gfid))
                        formId = gfid.GetString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Graph API lead fetch failed: {Msg}", ex.Message);
                // Fail hone par bhi basic data save karte hain
            }

            _context.Leads.Add(new Lead
            {
                LeadId = leadId,
                PageId = pageId,
                FormId = formId,
                FullName = fullName,
                Email = email,
                Phone = phone,
                Platform = platform,
                RawData = rawData,
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Lead saved — id={Id} name={Name} email={Email}",
                leadId, fullName, email);
        }

        // ══════════════════════════════════════════════════════
        // COMMENT save
        // ══════════════════════════════════════════════════════
        private async Task SaveComment(JsonElement value, string? pageId, string platform, string type)
        {
            var commentId = value.TryGetProperty("comment_id", out var cid) ? cid.GetString()
                          : value.TryGetProperty("id", out var id) ? id.GetString()
                          : Guid.NewGuid().ToString();

            if (await _context.PageComments.AnyAsync(c => c.CommentId == commentId))
                return;

            var senderId = value.TryGetProperty("from", out var from)
                && from.TryGetProperty("id", out var fid) ? fid.GetString() : null;
            var senderName = value.TryGetProperty("from", out var from2)
                && from2.TryGetProperty("name", out var fn) ? fn.GetString() : null;
            var msg = value.TryGetProperty("message", out var m) ? m.GetString() : null;
            var postId = value.TryGetProperty("post_id", out var pid) ? pid.GetString() : null;
            var ts = value.TryGetProperty("created_time", out var t) ? t.GetInt64() : 0;

            _context.PageComments.Add(new PageComment
            {
                CommentId = commentId!,
                PageId = pageId,
                PostId = postId,
                SenderId = senderId,
                SenderName = senderName,
                Message = msg,
                Platform = platform,
                CommentType = type,
                CommentTime = ts > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime
                    : DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Comment saved — platform={P} type={T} sender={S}",
                platform, type, senderName);
        }

        // ══════════════════════════════════════════════════════
        // POST INSIGHT update
        // ══════════════════════════════════════════════════════
        private async Task UpdatePostInsight(string postId, string? pageId, string platform, string action)
        {
            var insight = await _context.PostInsights
                .FirstOrDefaultAsync(p => p.PostId == postId && p.Platform == platform);

            if (insight == null)
            {
                insight = new PostInsight
                {
                    PostId = postId,
                    PageId = pageId,
                    Platform = platform,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.PostInsights.Add(insight);
            }

            switch (action)
            {
                case "like": insight.LikesCount++; break;
                case "comment": insight.CommentsCount++; break;
                case "share": insight.SharesCount++; break;
            }

            insight.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        // ══════════════════════════════════════════════════════
        // WEBHOOK EVENT LOG
        // ══════════════════════════════════════════════════════
        private async Task LogWebhookEvent(string platform, string rawPayload)
        {
            _context.WebhookEvents.Add(new WebhookEvent
            {
                Platform = platform,
                EventType = "incoming",
                RawPayload = rawPayload,
                Processed = false,
                ReceivedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
        }

        // ══════════════════════════════════════════════════════
        // DB se page access token fetch
        // ══════════════════════════════════════════════════════
        private async Task<string?> GetPageAccessToken(string? pageId)
        {
            if (string.IsNullOrEmpty(pageId)) return null;

            var account = await _context.ConnectedAccounts
                .Where(a => a.AccountId == pageId && a.IsActive)
                .FirstOrDefaultAsync();

            return account?.AccessToken;
        }

        // ══════════════════════════════════════════════════════
        // HMAC SHA256 verify
        // ══════════════════════════════════════════════════════
        private bool VerifySignature(string payload, string? signatureHeader)
        {
            if (string.IsNullOrEmpty(signatureHeader)) return false;

            var appSecret = _config["Meta:AppSecret"];
            if (string.IsNullOrEmpty(appSecret))
            {
                _logger.LogWarning("AppSecret not set — skipping (dev mode)");
                return true; // Dev mein skip karo
            }

            var signature = signatureHeader.Replace("sha256=", "");

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var expected = BitConverter.ToString(hash).Replace("-", "").ToLower();

            return signature.ToLower() == expected;
        }
    }
}