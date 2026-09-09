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
    /// There's no OAuth consent screen for this product the way there is
    /// for Facebook/Instagram/Twitter/LinkedIn — a business generates its
    /// own Phone Number ID and a permanent access token in Meta Business
    /// Manager and provides them directly, so every method here takes the
    /// caller's own accessToken/phoneNumberId rather than resolving one
    /// from a stored app-level secret.
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
