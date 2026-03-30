using Azure.Core;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace SocialMediaPanel.Services
{
    public class InstagramService
    {
        private readonly IConfiguration _config;
        private readonly HttpClient _http;

        public InstagramService(IConfiguration config, IHttpClientFactory factory)
        {
            _config = config;
            _http = factory.CreateClient("Facebook"); // same client
        }

        private string AppId => _config["Instagram:AppId"]!;
        private string AppSecret => _config["Instagram:AppSecret"]!;
        private string RedirectUri => "https://localhost:7276/Integrations/Callback/instagram";

        // ✅ Instagram scopes (IMPORTANT)
        private string Scope =
    "instagram_business_basic," +
    "instagram_business_manage_comments," +
    "instagram_business_manage_insights," +
    "instagram_business_content_publish," +
    "instagram_business_manage_messages";
        // ── Step 1: OAuth URL ───────────────────────────────
        public string BuildOAuthUrl(string state)
        {
            // ✅ www.instagram.com NOT api.instagram.com
            return "https://www.instagram.com/oauth/authorize"
                + $"?client_id={Uri.EscapeDataString(AppId)}"
                + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
                + $"&scope={Uri.EscapeDataString(Scope)}"
                + $"&state={Uri.EscapeDataString(state)}"
                + "&response_type=code"
                + "&force_reauth=true";  // optional but good UX
        }

        // ── Step 2: Exchange Token (same as FB) ─────────────
        public async Task<string> ExchangeCodeAsync(string code)
        {
            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("client_id", AppId),
                new KeyValuePair<string,string>("client_secret", AppSecret),
                new KeyValuePair<string,string>("grant_type", "authorization_code"),
                new KeyValuePair<string,string>("redirect_uri", RedirectUri),
                new KeyValuePair<string,string>("code", code)
            });

            var res = await _http.PostAsync("https://api.instagram.com/oauth/access_token", body);
            res.EnsureSuccessStatusCode();

            var json = await res.Content.ReadAsStringAsync();
            var payload = JsonSerializer.Deserialize<IgTokenPayload>(json);

            return payload?.AccessToken ?? throw new Exception("Token exchange failed");
        }

        // ── Step 3: Exchange for long-lived token (60 days) ─
        // ── Step 3: Get long-lived token — with fallback ───
        public async Task<string> GetLongLivedTokenAsync(string shortLivedToken)
        {
            try
            {
                var url = "https://graph.instagram.com/access_token"
    + $"?grant_type=ig_exchange_token"
    + $"&client_secret={AppSecret}"
    + $"&access_token={shortLivedToken}";

                var res = await _http.GetAsync(url);
                var json = await res.Content.ReadAsStringAsync();
                Console.WriteLine("LONG TOKEN RESPONSE: " + json);
                res.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("access_token").GetString();
            }
            catch
            {
                return shortLivedToken; // fallback
            }
        }

        // ── Step 3: Get Instagram Business Account ─────────
        public async Task<InstagramAccount> GetInstagramAccountsAsync(string accessToken)
        {
            // Uses graph.instagram.com NOT graph.facebook.com
            var fields = "id,username,name,profile_picture_url";
            var url = $"https://graph.instagram.com/me?fields={fields}&access_token={accessToken}";
            var res = await _http.GetAsync(url);
            res.EnsureSuccessStatusCode();

            var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = json.RootElement;

            return new InstagramAccount
            {
                InstagramId = root.GetProperty("id").GetString()!,
                Username = root.GetProperty("username").GetString()!,
                ProfilePicture = root.TryGetProperty("profile_picture_url", out var pic)
                    ? pic.GetString() ?? "" : "",
                AccessToken = accessToken  // store this directly, no page token needed
            };
        }

        // ── Reply to comment ───────────────────────────────
        public async Task<(bool Success, string Error)> ReplyToCommentAsync(
            string commentId, string message, string accessToken)
        {
            // Now uses graph.instagram.com
            var url = $"https://graph.instagram.com/v21.0/{commentId}/replies";

            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("message", message),
                new KeyValuePair<string,string>("access_token", accessToken)
            });

            var res = await _http.PostAsync(url, body);
            var json = await res.Content.ReadAsStringAsync();

            return res.IsSuccessStatusCode ? (true, "") : (false, json);
        }
    }

    public class InstagramAccount
    {
        public string InstagramId { get; set; } = "";
        public string Username { get; set; } = "";
        public string ProfilePicture { get; set; } = "";
        public string AccessToken { get; set; } = "";  // No more PageAccessToken!
    }

    internal class IgTokenPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("user_id")]
        public long UserId { get; set; }
    }

    internal class IgLongLivedTokenPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}