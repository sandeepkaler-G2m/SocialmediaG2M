using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SocialMediaPanel.Services
{
    /// <summary>
    /// Real WhatsApp Business Cloud API (Meta's official product) client.
    /// Not to be confused with WhatsAppSendService, which wraps a separate
    /// third-party relay (go2market.ai) used only by the standalone
    /// WhatsApp-PDF Mail-Merge feature.
    ///
    /// G2M is a Meta Tech Provider / Solution Partner — end-users onboard
    /// through WhatsApp Embedded Signup (a "Connect with Facebook" button)
    /// rather than pasting their own Phone Number ID/access token. The
    /// frontend runs FB.login with G2M's own Configuration ID and hands
    /// back a short-lived authorization code; ExchangeCodeForBusinessTokenAsync/
    /// RegisterPhoneNumberAsync/SubscribeToWebhooksAsync below complete that
    /// flow server-side using G2M's own App ID/Secret. The per-message
    /// methods (SendTextMessageAsync etc.) still take a phoneNumberId/
    /// accessToken per call since each onboarded customer gets their own
    /// Business Integration System User token, scoped to their own WABA.
    /// </summary>
    public class WhatsAppBusinessService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _config;

        public WhatsAppBusinessService(IHttpClientFactory httpFactory, IConfiguration config)
        {
            _httpFactory = httpFactory;
            _config = config;
        }

        private string ApiVersion => _config["WhatsApp:GraphApiVersion"] ?? "v21.0";
        private string AppId => _config["WhatsApp:AppId"] ?? "";
        private string AppSecret => _config["WhatsApp:AppSecret"] ?? "";
        public string ConfigurationId => _config["WhatsApp:ConfigurationId"] ?? "";

        // ── Embedded Signup step 1: exchange the short-lived code the
        // frontend received via postMessage for a Business Integration
        // System User access token, scoped to the customer's assets.
        // No redirect_uri — this isn't a redirect-based OAuth flow. ──────
        public async Task<(bool Success, string? AccessToken, string Error)> ExchangeCodeForBusinessTokenAsync(string code)
        {
            var client = _httpFactory.CreateClient();
            var url = $"https://graph.facebook.com/{ApiVersion}/oauth/access_token" +
                      $"?client_id={Uri.EscapeDataString(AppId)}" +
                      $"&client_secret={Uri.EscapeDataString(AppSecret)}" +
                      $"&code={Uri.EscapeDataString(code)}";

            var resp = await client.GetAsync(url);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return (false, null, json);

            using var doc = JsonDocument.Parse(json);
            var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(token))
                return (false, null, json);

            return (true, token, "");
        }

        // ── Embedded Signup step 2: register the customer's phone number
        // for Cloud API use. Required once per phone number before it can
        // send/receive. A fixed 6-digit PIN is fine — it's only used for
        // 2-step verification if the number is ever re-registered elsewhere. ──
        public async Task<(bool Success, string Error)> RegisterPhoneNumberAsync(string phoneNumberId, string accessToken, string pin = "000000")
        {
            var payload = new { messaging_product = "whatsapp", pin };
            var client = _httpFactory.CreateClient();
            var url = $"https://graph.facebook.com/{ApiVersion}/{phoneNumberId}/register";
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var resp = await client.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            return resp.IsSuccessStatusCode ? (true, "") : (false, json);
        }

        // ── Embedded Signup step 3: subscribe G2M's app to webhook events
        // on the customer's WABA (messages, statuses, etc.). Required once
        // per WABA — without this, no webhook events arrive for it at all. ──
        public async Task<(bool Success, string Error)> SubscribeToWebhooksAsync(string wabaId, string accessToken)
        {
            var client = _httpFactory.CreateClient();
            var url = $"https://graph.facebook.com/{ApiVersion}/{wabaId}/subscribed_apps";
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("", Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var resp = await client.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            return resp.IsSuccessStatusCode ? (true, "") : (false, json);
        }

        // ── Verify credentials + fetch display info at connect time ──────
        public async Task<(bool Success, string? DisplayPhoneNumber, string? VerifiedName, string Error)>
            GetPhoneNumberInfoAsync(string phoneNumberId, string accessToken)
        {
            var client = _httpFactory.CreateClient();
            var url = $"https://graph.facebook.com/{ApiVersion}/{phoneNumberId}" +
                      "?fields=display_phone_number,verified_name";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var resp = await client.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return (false, null, null, json);

            using var doc = JsonDocument.Parse(json);
            var displayNumber = doc.RootElement.TryGetProperty("display_phone_number", out var dp) ? dp.GetString() : null;
            var verifiedName = doc.RootElement.TryGetProperty("verified_name", out var vn) ? vn.GetString() : null;
            return (true, displayNumber, verifiedName, "");
        }

        // ── Send a plain text message ─────────────────────────────────────
        // Only works within Meta's 24-hour customer-service window (a message
        // must have come in from this number within the last 24h) — same rule
        // as Facebook Messenger and Instagram DMs. Outside that window, Meta
        // requires an approved template message instead (SendTemplateAsync).
        public async Task<(bool Success, string? MessageId, string Error)> SendTextMessageAsync(
            string phoneNumberId, string accessToken, string to, string text)
        {
            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "text",
                text = new { body = text }
            };
            return await PostMessageAsync(phoneNumberId, accessToken, payload);
        }

        // ── Send an approved template message (works outside the 24h window) ──
        public async Task<(bool Success, string? MessageId, string Error)> SendTemplateAsync(
            string phoneNumberId, string accessToken, string to, string templateName,
            string languageCode = "en_US", List<string>? bodyParams = null)
        {
            object payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "template",
                template = new
                {
                    name = templateName,
                    language = new { code = languageCode },
                    components = (bodyParams != null && bodyParams.Count > 0)
                        ? new[] { new { type = "body", parameters = bodyParams.Select(p => new { type = "text", text = p }).ToArray() } }
                        : null
                }
            };
            return await PostMessageAsync(phoneNumberId, accessToken, payload);
        }

        private async Task<(bool Success, string? MessageId, string Error)> PostMessageAsync(
            string phoneNumberId, string accessToken, object payload)
        {
            var client = _httpFactory.CreateClient();
            var url = $"https://graph.facebook.com/{ApiVersion}/{phoneNumberId}/messages";
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var resp = await client.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return (false, null, json);

            using var doc = JsonDocument.Parse(json);
            var messageId = doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() > 0
                ? msgs[0].TryGetProperty("id", out var idProp) ? idProp.GetString() : null
                : null;

            return (true, messageId, "");
        }

        // ── Mark an incoming message as read (blue ticks) ─────────────────
        public async Task MarkAsReadAsync(string phoneNumberId, string accessToken, string messageId)
        {
            try
            {
                var client = _httpFactory.CreateClient();
                var url = $"https://graph.facebook.com/{ApiVersion}/{phoneNumberId}/messages";
                var payload = new { messaging_product = "whatsapp", status = "read", message_id = messageId };
                var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                await client.SendAsync(req);
            }
            catch { /* best-effort — a failed read-receipt shouldn't break anything else */ }
        }
    }
}
