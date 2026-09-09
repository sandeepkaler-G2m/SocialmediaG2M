using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocialMediaPanel.Services
{
    /// <summary>
    /// Standalone helper for the WhatsApp-PDF-relay feature (see WhatsAppPdfController).
    /// Wraps the go2market.ai "clouds3/send" API. Nothing here is a fixed/hardcoded
    /// curl — every field on <see cref="WhatsAppSendRequest"/> is caller-supplied per
    /// call; only the send URL and a fallback apiKey come from config
    /// (WhatsAppRelay:SendUrl / WhatsAppRelay:DefaultApiKey) so a request can omit
    /// apiKey and still work.
    /// </summary>
    public class WhatsAppSendService
    {
        private readonly HttpClient _http;
        private readonly IConfiguration _config;
        private readonly ILogger<WhatsAppSendService> _logger;

        public WhatsAppSendService(HttpClient http, IConfiguration config, ILogger<WhatsAppSendService> logger)
        {
            _http = http;
            _config = config;
            _logger = logger;
        }

        public class WhatsAppMedia
        {
            [JsonPropertyName("url")]
            public string Url { get; set; } = "";
        }

        public class WhatsAppSendPayload
        {
            [JsonPropertyName("apiKey")]
            public string ApiKey { get; set; } = "";

            [JsonPropertyName("campaignName")]
            public string CampaignName { get; set; } = "";

            [JsonPropertyName("destination")]
            public string Destination { get; set; } = "";

            [JsonPropertyName("type")]
            public string Type { get; set; } = "template";

            [JsonPropertyName("mediatype")]
            public string MediaType { get; set; } = "DOCUMENT";

            [JsonPropertyName("templateParams")]
            public List<string> TemplateParams { get; set; } = new();

            [JsonPropertyName("media")]
            public WhatsAppMedia Media { get; set; } = new();
        }

        public async Task<(bool Success, int StatusCode, string RawResponse)> SendAsync(
            WhatsAppSendPayload payload, CancellationToken ct = default)
        {
            var sendUrl = _config["WhatsAppRelay:SendUrl"];
            if (string.IsNullOrWhiteSpace(sendUrl))
                throw new InvalidOperationException("WhatsAppRelay:SendUrl configured nahi hai (appsettings.json).");

            if (string.IsNullOrWhiteSpace(payload.ApiKey))
                payload.ApiKey = _config["WhatsAppRelay:DefaultApiKey"] ?? "";

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.Never
            });

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(sendUrl, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            _logger.LogInformation(
                "WhatsApp send: destination={Destination} campaign={Campaign} status={Status}",
                payload.Destination, payload.CampaignName, (int)resp.StatusCode);

            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
    }
}
