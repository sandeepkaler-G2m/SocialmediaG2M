using Azure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using SocialMediaPanel.Data;
using SocialMediaPanel.Hubs;
using SocialMediaPanel.Models;
using SocialMediaPanel.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JsonException = Newtonsoft.Json.JsonException;

namespace SocialMediaPanel.Controllers
{
    [Route("webhook")]
    [ApiController]
    public class WebhookController : ControllerBase
    {
        private readonly ILogger<WebhookController> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;
        private readonly IHubContext<InboxHub> _hub;

        public WebhookController(
            ILogger<WebhookController> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration config,
            IHubContext<InboxHub> hub)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _config = config;
            _hub = hub;
        }

        // ══════════════════════════════════════════════════════
        // GET /webhook
        // Meta verification — pehli baar subscribe karte waqt
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
                _logger.LogInformation("Webhook verified OK ✅");
                return Content(challenge ?? "", "text/plain");
            }

            _logger.LogWarning("Webhook verification FAILED ❌ — check verify token");
            // Forbid() invokes the authentication service's ForbidAsync, which
            // needs a configured auth scheme — this app uses custom session
            // auth, not ASP.NET Core authentication, so that throws. A plain
            // 403 status is all Meta's webhook verification actually needs.
            return StatusCode(403, "Forbidden");
        }

        // ══════════════════════════════════════════════════════
        // POST /webhook
        // RULE: Meta ko TURANT 200 OK — 5 sec timeout hai
        // Background mein sab process karo
        // ══════════════════════════════════════════════════════
        [HttpPost]
        public async Task<IActionResult> Receive()
        {
            string rawBody;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                rawBody = await reader.ReadToEndAsync();
            }

            _logger.LogInformation(
                "Webhook POST received — length={Len} preview={Preview}",
                rawBody.Length,
                rawBody[..Math.Min(100, rawBody.Length)]);

            // Background mein process karo — new scope ke saath
            _ = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<WebhookController>>();

                try
                {
                    await ProcessWebhook(rawBody, context, httpFactory, config, logger, _hub);
                }
                catch (Exception ex)
                {
                    logger.LogError("Webhook processing error: {Msg}\n{Stack}", ex.Message, ex.StackTrace);
                }
            });

            // Meta ko TURANT 200 — warna retry karta hai
            return Ok("EVENT_RECEIVED");
        }

        // ══════════════════════════════════════════════════════
        // MAIN PROCESSOR
        // ══════════════════════════════════════════════════════
        private static async Task ProcessWebhook(
            string rawBody,
            AppDbContext context,
            IHttpClientFactory httpFactory,
            IConfiguration config,
            ILogger logger,
            IHubContext<InboxHub> hub)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(rawBody);
            }
            catch (JsonException ex)
            {
                logger.LogError("JSON parse error: {Msg}", ex.Message);
                return;
            }

            var root = doc.RootElement;
            var obj = root.TryGetProperty("object", out var op) ? op.GetString() : "";
            var hasEntry = root.TryGetProperty("entry", out var entries);

            logger.LogInformation("Processing webhook — object={Obj}", obj);

            // Saara raw event log karo debug ke liye
            await LogWebhookEvent(obj ?? "unknown", rawBody, context);

            if (!hasEntry) return;

            foreach (var entry in entries.EnumerateArray())
            {
                try
                {
                    switch (obj?.ToLower())
                    {
                        case "page":
                            await HandlePageEvent(entry, context, httpFactory, config, logger, hub);
                            break;

                        case "instagram":
                            await HandleInstagramEvent(entry, context, httpFactory, config, logger, hub);
                            break;

                        case "ad_account":
                            await HandleAdAccountEvent(entry, context, httpFactory, logger);
                            break;

                        default:
                            logger.LogInformation("Unhandled object type: {Obj}", obj);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError("Entry processing error: {Msg}", ex.Message);
                    // Ek entry fail hone par baaki process hoti rahe
                }
            }
        }

        // ══════════════════════════════════════════════════════
        // FACEBOOK PAGE EVENTS
        // Fields: leadgen, feed, messages, mention, ratings
        // ══════════════════════════════════════════════════════
        private static async Task HandlePageEvent(
            JsonElement entry,
            AppDbContext context,
            IHttpClientFactory httpFactory,
            IConfiguration config,
            ILogger logger,
            IHubContext<InboxHub> hub)
        {
            var pageId = entry.TryGetProperty("id", out var idP) ? idP.GetString() : null;

            // Direct Messenger messages (entry ke andar messaging array)
            if (entry.TryGetProperty("messaging", out var directMsg))
                await HandleMessaging(directMsg, pageId, "messenger", context, httpFactory, logger, hub);

            if (!entry.TryGetProperty("changes", out var changes)) return;

            foreach (var change in changes.EnumerateArray())
            {
                var field = change.TryGetProperty("field", out var f) ? f.GetString() : "";
                var value = change.TryGetProperty("value", out var v) ? v : (JsonElement?)null;

                logger.LogInformation("Page field={Field} pageId={PageId}", field, pageId);

                try
                {
                    switch (field?.ToLower())
                    {
                        // ── LEAD ADS ──
                        case "leadgen":
                            if (value.HasValue)
                                await FetchAndSaveLead(value.Value, pageId, "facebook", context, httpFactory, logger);
                            break;

                        // ── POST FEED (likes, comments, shares, reactions) ──
                        case "feed":
                            if (value.HasValue)
                                await HandleFeedChange(value.Value, pageId, "facebook", context, httpFactory, config, logger, hub);
                            break;

                        // ── MESSENGER ──
                        case "messages":
                            if (entry.TryGetProperty("messaging", out var msg))
                                await HandleMessaging(msg, pageId, "messenger", context, httpFactory, logger, hub);
                            break;

                        // ── PAGE MENTION ──
                        case "mention":
                            if (value.HasValue)
                                await SaveComment(value.Value, pageId, "facebook", "mention", context, httpFactory, config, logger, hub);
                            break;

                        case "ratings":
                            logger.LogInformation("Page rating — pageId={PageId}", pageId);
                            break;

                        default:
                            logger.LogInformation("Unhandled page field: {Field}", field);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError("Field={Field} error: {Msg}", field, ex.Message);
                }
            }
        }

        // ══════════════════════════════════════════════════════
        // INSTAGRAM EVENTS
        // Fields: comments, mentions, messages, leadgen, story_insights
        // ══════════════════════════════════════════════════════
        private static async Task HandleInstagramEvent(
            JsonElement entry,
            AppDbContext context,
            IHttpClientFactory httpFactory,
            IConfiguration config,
            ILogger logger,
            IHubContext<InboxHub> hub)
        {
            var igId = entry.TryGetProperty("id", out var idP) ? idP.GetString() : null;

            // Instagram Direct Messages
            if (entry.TryGetProperty("messaging", out var messaging))
                await HandleMessaging(messaging, igId, "instagram_dm", context, httpFactory, logger, hub);

            if (!entry.TryGetProperty("changes", out var changes)) return;

            foreach (var change in changes.EnumerateArray())
            {
                var field = change.TryGetProperty("field", out var f) ? f.GetString() : "";
                var value = change.TryGetProperty("value", out var v) ? v : (JsonElement?)null;

                logger.LogInformation("Instagram field={Field} igId={IgId}", field, igId);

                try
                {
                    switch (field?.ToLower())
                    {
                        case "comments":
                            if (value.HasValue)
                                await SaveComment(value.Value, igId, "instagram", "comment", context, httpFactory, config, logger, hub);
                            break;

                        case "mentions":
                            if (value.HasValue)
                                await SaveComment(value.Value, igId, "instagram", "mention", context, httpFactory, config, logger, hub);
                            break;

                        case "live_comments":
                            if (value.HasValue)
                                await SaveComment(value.Value, igId, "instagram", "live_comment", context, httpFactory, config, logger, hub);
                            break;

                        case "leadgen":
                            if (value.HasValue)
                                await FetchAndSaveLead(value.Value, igId, "instagram", context, httpFactory, logger);
                            break;

                        case "story_insights":
                            logger.LogInformation("Story insight — igId={IgId}", igId);
                            break;

                        default:
                            logger.LogInformation("Unhandled Instagram field: {Field}", field);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError("IG Field={Field} error: {Msg}", field, ex.Message);
                }
            }
        }

        // ══════════════════════════════════════════════════════
        // AD ACCOUNT EVENTS
        // ══════════════════════════════════════════════════════
        private static async Task HandleAdAccountEvent(
            JsonElement entry,
            AppDbContext context,
            IHttpClientFactory httpFactory,
            ILogger logger)
        {
            var adId = entry.TryGetProperty("id", out var idP) ? idP.GetString() : null;

            if (!entry.TryGetProperty("changes", out var changes)) return;

            foreach (var change in changes.EnumerateArray())
            {
                var field = change.TryGetProperty("field", out var f) ? f.GetString() : "";
                var value = change.TryGetProperty("value", out var v) ? v : (JsonElement?)null;

                if ((field == "lead_gen" || field == "leadgen") && value.HasValue)
                    await FetchAndSaveLead(value.Value, adId, "facebook_ad", context, httpFactory, logger);
            }
        }

        // ══════════════════════════════════════════════════════
        // FEED CHANGE HANDLER
        //
        // Meta feed payload format:
        // {
        //   "item": "comment|reaction|share|status|photo|video",
        //   "verb": "add|remove|edited",
        //   "post_id": "PAGE_ID_POST_ID",
        //   "comment_id": "...",    (sirf comment mein)
        //   "reaction_type": "like|love|haha|wow|sad|angry"
        // }
        // ══════════════════════════════════════════════════════
        private static async Task HandleFeedChange(
            JsonElement value,
            string? pageId,
            string platform,
            AppDbContext context,
            IHttpClientFactory httpFactory,
            IConfiguration config,
            ILogger logger,
            IHubContext<InboxHub> hub)
        {
            var item = value.TryGetProperty("item", out var i) ? i.GetString() : "";
            var verb = value.TryGetProperty("verb", out var vb) ? vb.GetString() : "";
            var postId = value.TryGetProperty("post_id", out var pid) ? pid.GetString() : null;
            var commentId = value.TryGetProperty("comment_id", out var cid) ? cid.GetString() : null;

            logger.LogInformation(
                "Feed — item={Item} verb={Verb} postId={PostId}",
                item, verb, postId);

            switch (item?.ToLower())
            {
                // ── COMMENT ──
                case "comment":
                    if (verb == "add")
                    {
                        await SaveComment(value, pageId, platform, "comment", context, httpFactory, config, logger, hub);

                        // Post ka comment count bhi update karo
                        if (postId != null)
                            await UpdatePostInsight(postId, pageId, platform, "comment", context);
                    }
                    else if (verb == "remove" && postId != null)
                    {
                        // Comment hata — count kam karo
                        await UpdatePostInsight(postId, pageId, platform, "comment_remove", context);
                    }
                    break;

                // ── REACTION / LIKE ──
                case "reaction":
                case "like":
                    if (postId != null)
                    {
                        if (verb == "add")
                            await UpdatePostInsight(postId, pageId, platform, "like", context);
                        else if (verb == "remove")
                            await UpdatePostInsight(postId, pageId, platform, "like_remove", context);
                    }
                    break;

                // ── SHARE ──
                // Meta share 2 tarike se aata hai:
                // 1. item=share, verb=add
                // 2. item=status, verb=add (reshared post)
                case "share":
                    if (verb == "add" && postId != null)
                    {
                        logger.LogInformation("Share detected — postId={PostId}", postId);
                        await UpdatePostInsight(postId, pageId, platform, "share", context);
                    }
                    break;

                // ── NEW POST / PHOTO / VIDEO ──
                case "status":
                case "photo":
                case "video":
                case "link":
                    if (verb == "add")
                    {
                        logger.LogInformation(
                            "New {Item} posted — postId={PostId}", item, postId);

                        // Naya post — insight record banao
                        if (postId != null)
                            await EnsurePostInsightExists(postId, pageId, platform, context);
                    }
                    else if (verb == "edited")
                    {
                        logger.LogInformation("Post edited — postId={PostId}", postId);
                    }
                    else if (verb == "remove")
                    {
                        logger.LogInformation("Post removed — postId={PostId}", postId);
                    }
                    break;

                default:
                    logger.LogInformation("Unhandled feed item: {Item}", item);
                    break;
            }
        }

        // ══════════════════════════════════════════════════════
        // MESSAGING HANDLER
        // Messenger + Instagram DM dono yahan handle hote hain
        // ══════════════════════════════════════════════════════
        private static async Task HandleMessaging(
            JsonElement messaging,
            string? pageId,
            string platform,
            AppDbContext context,
            IHttpClientFactory httpFactory,
            ILogger logger,
            IHubContext<InboxHub> hub)
        {
            foreach (var msgEvent in messaging.EnumerateArray())
            {
                try
                {
                    // Sirf message events handle karo
                    if (!msgEvent.TryGetProperty("message", out var message)) continue;

                    // Page ka apna echo message skip karo
                    if (message.TryGetProperty("is_echo", out var echo) && echo.GetBoolean())
                        continue;

                    // Reactions/edits/deletes on an EXISTING message reuse that
                    // message's own "mid" — without this check they'd collide
                    // with the original message's unique message_id and, worse,
                    // abort the whole batch's save for every other real message
                    // in the same webhook delivery. Only a genuine new message
                    // (has text or an attachment, no reaction payload) counts.
                    var hasReaction = message.TryGetProperty("reaction", out _);
                    var hasText = message.TryGetProperty("text", out var txt) && !string.IsNullOrEmpty(txt.GetString());
                    var hasAttachments = message.TryGetProperty("attachments", out _);
                    if (hasReaction || (!hasText && !hasAttachments))
                    {
                        logger.LogInformation("Skipping non-content messaging sub-event (reaction/seen/edit)");
                        continue;
                    }

                    var senderId = msgEvent.TryGetProperty("sender", out var s)
                        && s.TryGetProperty("id", out var sid) ? sid.GetString() : null;
                    var messageId = message.TryGetProperty("mid", out var mid)
                        ? mid.GetString() : Guid.NewGuid().ToString();
                    var text = hasText ? txt.GetString() : null;
                    var timestamp = msgEvent.TryGetProperty("timestamp", out var ts)
                        ? ts.GetInt64() : 0;

                    var senderName = await FetchSenderNameAsync(senderId, pageId, platform, context, httpFactory, logger);
                    var messageTime = timestamp > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime
                        : DateTime.UtcNow;

                    // Meta double-delivers webhook events (confirmed live — two
                    // near-simultaneous POSTs for the same message, each in its
                    // own DbContext/connection). EF Core's Add()+SaveChanges()
                    // can't survive that race cleanly: MySql.EntityFrameworkCore
                    // wraps the loser's duplicate-key violation in an opaque
                    // "Could not save changes" DbUpdateException, and once that's
                    // thrown the same context's connection/transaction state is
                    // unreliable for a same-context retry (tried that — still
                    // failed). A raw "INSERT IGNORE" sidesteps EF's tracking
                    // entirely and lets MySQL itself resolve the race atomically:
                    // the loser is a silent no-op (0 rows affected) instead of an
                    // exception, and the real message is never lost.
                    var rowsAffected = await context.Database.ExecuteSqlInterpolatedAsync($@"
                        INSERT IGNORE INTO page_messages
                            (message_id, page_id, sender_id, sender_name, message_text, platform, message_time, is_replied, created_at)
                        VALUES
                            ({messageId}, {pageId}, {senderId}, {senderName}, {text}, {platform}, {messageTime}, 0, {DateTime.UtcNow})");

                    if (rowsAffected == 0)
                    {
                        logger.LogInformation("Message already exists — skip: {MsgId}", messageId);
                        continue;
                    }

                    await hub.Clients.All.SendAsync("inboxChanged");

                    logger.LogInformation(
                        "Message saved ✅ — platform={P} sender={S} text={T}",
                        platform, senderId, text?[..Math.Min(30, text?.Length ?? 0)]);
                }
                catch (Exception ex)
                {
                    logger.LogError("Message save error: {Msg}", ex.Message);
                }
            }
        }

        // Best-effort sender display name lookup. Messenger locks down PSID
        // profile fields heavily (Meta restricted this after 2018), so a null
        // result there is expected and the UI falls back to "User <id>".
        // Instagram DM (IGSID) generally does return name/username.
        private static async Task<string?> FetchSenderNameAsync(
            string? senderId, string? pageId, string platform,
            AppDbContext context, IHttpClientFactory httpFactory, ILogger logger)
        {
            if (string.IsNullOrEmpty(senderId) || string.IsNullOrEmpty(pageId)) return null;

            var tokenPlatform = platform == "instagram_dm" ? "instagram" : "facebook";
            var token = await ResolvePageTokenForHide(pageId, tokenPlatform, context, httpFactory);
            if (string.IsNullOrEmpty(token)) return null;

            try
            {
                var client = httpFactory.CreateClient();
                var fields = platform == "instagram_dm" ? "name,username" : "first_name,last_name";
                var url = $"https://graph.facebook.com/v19.0/{senderId}?fields={fields}&access_token={token}";
                var body = await client.GetStringAsync(url);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (platform == "instagram_dm")
                {
                    if (root.TryGetProperty("username", out var un) && un.GetString() is string username)
                        return username;
                    if (root.TryGetProperty("name", out var nm) && nm.GetString() is string name)
                        return name;
                }
                else
                {
                    var first = root.TryGetProperty("first_name", out var f) ? f.GetString() : null;
                    var last = root.TryGetProperty("last_name", out var l) ? l.GetString() : null;
                    var full = $"{first} {last}".Trim();
                    if (!string.IsNullOrWhiteSpace(full)) return full;
                }
            }
            catch (Exception ex)
            {
                logger.LogInformation("Sender name lookup failed for {SenderId}: {Msg}", senderId, ex.Message);
            }

            return null;
        }

        // ══════════════════════════════════════════════════════
        // LEAD FETCH + SAVE
        // Webhook sirf leadgen_id deta hai
        // Graph API se field_data (name, email, phone) fetch karo
        // ══════════════════════════════════════════════════════
        private static async Task FetchAndSaveLead(
            JsonElement value,
            string? pageId,
            string platform,
            AppDbContext context,
            IHttpClientFactory httpFactory,
            ILogger logger)
        {
            // Lead ID lo
            var leadId = value.TryGetProperty("leadgen_id", out var lid) ? lid.GetString()
                       : value.TryGetProperty("lead_id", out var lid2) ? lid2.GetString()
                       : null;
            var formId = value.TryGetProperty("form_id", out var fid) ? fid.GetString() : null;

            if (string.IsNullOrEmpty(leadId))
            {
                logger.LogWarning("Lead ID nahi mila — skip");
                return;
            }

            // Duplicate check
            if (await context.Leads.AnyAsync(l => l.LeadId == leadId))
            {
                logger.LogInformation("Lead already exists — skip: {LeadId}", leadId);
                return;
            }

            string? fullName = null, email = null, phone = null;
            string rawData = value.GetRawText();

            try
            {
                // DB se page token lo
                // Pehle exact match, phir koi bhi active token
                var account = await context.ConnectedAccounts
                    .Where(a => a.AccountId == pageId && a.IsActive)
                    .FirstOrDefaultAsync()
                    ?? await context.ConnectedAccounts
                        .Where(a => a.IsActive)
                        .FirstOrDefaultAsync();

                var pageToken = account?.AccessToken;
                var userAccount = await context.UserTokens
    .Where(a => a.userId == 1) // ya session se userId le
    .OrderByDescending(a => a.Id)
    .FirstOrDefaultAsync();

                string? PageToken = null;

                // 🔥 Platform ke hisaab se token select karo
                if (platform == "instagram" || platform == "instagram_dm")
                {
                    pageToken = userAccount?.instagramtoken;
                }
                else
                {
                    pageToken = userAccount?.facebooktoken;
                }


                if (string.IsNullOrEmpty(pageToken))
                {
                    logger.LogWarning("Page token nahi mila — lead ID only save hoga: {LeadId}", leadId);
                }
                else
                {
                    var client = httpFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(10); // Timeout set karo

                    var url = $"https://graph.facebook.com/v19.0/{leadId}" +
                              $"?fields=field_data,created_time,ad_id,form_id,page_id" +
                              $"&access_token={pageToken}";

                    logger.LogInformation("Graph API lead fetch: {LeadId}", leadId);

                    var response = await client.GetStringAsync(url);
                    rawData = response;

                    using var leadDoc = JsonDocument.Parse(response);
                    var leadRoot = leadDoc.RootElement;

                    // Error check
                    if (leadRoot.TryGetProperty("error", out var error))
                    {
                        var errMsg = error.TryGetProperty("message", out var em) ? em.GetString() : "Unknown";
                        logger.LogWarning("Graph API error: {ErrMsg}", errMsg);
                        // Token expire — lead bina details save karo
                    }
                    else
                    {
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

                        // formId Graph se bhi le sakte hain
                        if (string.IsNullOrEmpty(formId)
                            && leadRoot.TryGetProperty("form_id", out var gfid))
                            formId = gfid.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Graph API lead fetch failed: {Msg} — saving with ID only", ex.Message);
            }

            // Lead save karo — chahe details mile ya na mile
            context.Leads.Add(new Lead
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

            await context.SaveChangesAsync();

            logger.LogInformation(
                "Lead saved ✅ — id={Id} name={Name} email={Email} phone={Phone}",
                leadId, fullName ?? "N/A", email ?? "N/A", phone ?? "N/A");
        }

        // ══════════════════════════════════════════════════════
        // COMMENT SAVE
        // ══════════════════════════════════════════════════════
        private static async Task SaveComment(
            JsonElement value,
            string? pageId,
            string platform,
            string type,
            AppDbContext context,
            IHttpClientFactory httpFactory,
            IConfiguration config,
            ILogger logger,
            IHubContext<InboxHub> hub)
        {
            // Comment ID lo — alag alag fields mein ho sakta hai
            var commentId = value.TryGetProperty("comment_id", out var cid) ? cid.GetString()
                          : value.TryGetProperty("id", out var id) ? id.GetString()
                          : Guid.NewGuid().ToString();

            // Duplicate check
            if (await context.PageComments.AnyAsync(c => c.CommentId == commentId))
            {
                logger.LogInformation("Comment already exists — skip: {CommentId}", commentId);
                return;
            }

            // Sender info
            var senderId = value.TryGetProperty("from", out var from)
                && from.TryGetProperty("id", out var fid) ? fid.GetString() : null;
            var senderName = value.TryGetProperty("from", out var from2)
                && from2.TryGetProperty("name", out var fn) ? fn.GetString() : null;

            // Instagram mein username hota hai name ki jagah
            if (string.IsNullOrEmpty(senderName))
                senderName = value.TryGetProperty("from", out var from3)
                    && from3.TryGetProperty("username", out var un) ? un.GetString() : null;

            var msg = value.TryGetProperty("message", out var m) ? m.GetString() : null;
            var postId = value.TryGetProperty("post_id", out var pid) ? pid.GetString() : null;
            var ts = value.TryGetProperty("created_time", out var t) ? t.GetInt64() : 0;

            context.PageComments.Add(new PageComment
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

            await context.SaveChangesAsync();
            await hub.Clients.All.SendAsync("inboxChanged");

            logger.LogInformation(
                "Comment saved ✅ — platform={P} type={T} sender={S} msg={M}",
                platform, type, senderName ?? "Unknown",
                msg?[..Math.Min(50, msg?.Length ?? 0)] ?? "");

            await AutoHideIfSpam(commentId!, msg, pageId, platform, context, httpFactory, config, logger);
        }

        // ══════════════════════════════════════════════════════
        // SPAM AUTO-HIDE
        // Checks a newly-saved comment's text against the configured
        // keyword list (appsettings.json → CommentModeration:BlockedKeywords)
        // and hides it via the Graph API on a match. Keyword-list based —
        // no DB table, so the operator edits appsettings.json to tune it.
        // ══════════════════════════════════════════════════════
        private static async Task AutoHideIfSpam(
            string commentId,
            string? message,
            string? pageId,
            string platform,
            AppDbContext context,
            IHttpClientFactory httpFactory,
            IConfiguration config,
            ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            var keywords = config.GetSection("CommentModeration:BlockedKeywords").Get<string[]>() ?? Array.Empty<string>();
            if (keywords.Length == 0) return;

            var matched = keywords.FirstOrDefault(k =>
                !string.IsNullOrWhiteSpace(k) && message.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (matched == null) return;

            var token = await ResolvePageTokenForHide(pageId, platform, context, httpFactory);
            if (string.IsNullOrEmpty(token))
            {
                logger.LogWarning("Spam match on comment {CommentId} (keyword '{Keyword}') but no page token resolved — could not hide.", commentId, matched);
                return;
            }

            try
            {
                var client = httpFactory.CreateClient();
                var resp = await client.PostAsync(
                    $"https://graph.facebook.com/v19.0/{commentId}?hide=true&access_token={token}", null);
                logger.LogInformation(
                    "Auto-hid spam comment {CommentId} (keyword '{Keyword}') — success={Success}",
                    commentId, matched, resp.IsSuccessStatusCode);
            }
            catch (Exception ex)
            {
                logger.LogError("Auto-hide error for comment {CommentId}: {Msg}", commentId, ex.Message);
            }
        }

        // Facebook comments: pageId is the actual FB Page ID, token comes straight
        // from FacebookPages. Instagram comments: pageId is the IG business account
        // ID, which isn't linked to a page by a foreign key (no migrations — see
        // ActivePageService), so each connected page's instagram_business_account
        // field is checked via the Graph API to find the owning page's token.
        private static async Task<string?> ResolvePageTokenForHide(
            string? pageId, string platform, AppDbContext context, IHttpClientFactory httpFactory)
        {
            if (string.IsNullOrEmpty(pageId)) return null;

            if (platform == "facebook")
            {
                var page = await context.FacebookPages.FirstOrDefaultAsync(p => p.page_id == pageId);
                return page?.page_access_token;
            }

            var pages = await context.FacebookPages.ToListAsync();
            var client = httpFactory.CreateClient();
            foreach (var page in pages)
            {
                try
                {
                    var url = $"https://graph.facebook.com/v19.0/{page.page_id}?fields=instagram_business_account&access_token={page.page_access_token}";
                    var body = await client.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("instagram_business_account", out var ig) &&
                        ig.TryGetProperty("id", out var idProp) &&
                        idProp.GetString() == pageId)
                    {
                        return page.page_access_token;
                    }
                }
                catch { /* try next page */ }
            }
            return null;
        }

        // ══════════════════════════════════════════════════════
        // POST INSIGHT UPDATE
        // Likes, comments, shares count update karo
        // ══════════════════════════════════════════════════════
        private static async Task UpdatePostInsight(
            string postId,
            string? pageId,
            string platform,
            string action,
            AppDbContext context)
        {
            var insight = await context.PostInsights
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
                context.PostInsights.Add(insight);
            }

            switch (action)
            {
                case "like":
                    insight.LikesCount++;
                    break;

                case "like_remove":
                    insight.LikesCount = Math.Max(0, insight.LikesCount - 1);
                    break;

                case "comment":
                    insight.CommentsCount++;
                    break;

                case "comment_remove":
                    insight.CommentsCount = Math.Max(0, insight.CommentsCount - 1);
                    break;

                case "share":
                    insight.SharesCount++;
                    break;
            }

            insight.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        // ══════════════════════════════════════════════════════
        // Naya post aane par empty insight record banao
        // ══════════════════════════════════════════════════════
        private static async Task EnsurePostInsightExists(
            string postId,
            string? pageId,
            string platform,
            AppDbContext context)
        {
            var exists = await context.PostInsights
                .AnyAsync(p => p.PostId == postId && p.Platform == platform);

            if (!exists)
            {
                context.PostInsights.Add(new PostInsight
                {
                    PostId = postId,
                    PageId = pageId,
                    Platform = platform,
                    UpdatedAt = DateTime.UtcNow
                });
                await context.SaveChangesAsync();
            }
        }

        // ══════════════════════════════════════════════════════
        // WEBHOOK EVENT LOG — Debug ke liye raw payload save
        // ══════════════════════════════════════════════════════
        private static async Task LogWebhookEvent(
            string platform,
            string rawPayload,
            AppDbContext context)
        {
            try
            {
                context.WebhookEvents.Add(new WebhookEvent
                {
                    Platform = platform,
                    EventType = "incoming",
                    RawPayload = rawPayload,
                    Processed = false,
                    ReceivedAt = DateTime.UtcNow
                });
                await context.SaveChangesAsync();
            }
            catch (Exception)
            {
                // Log fail hone par bhi processing continue karo
            }
        }
    }
}