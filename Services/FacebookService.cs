using Microsoft.Extensions.Configuration;

namespace SocialMediaPanel.Services
{
    /// <summary>
    /// Facebook OAuth service.
    /// Safe to register even when Facebook config keys are not yet set in appsettings.json —
    /// BuildOAuthUrl() will throw, which IntegrationsController catches gracefully.
    /// </summary>
    public class FacebookService
    {
        private readonly IConfiguration _config;
        private readonly HttpClient _http;

        public FacebookService(IConfiguration config, IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _http = httpClientFactory.CreateClient("Facebook");
        }

        private string AppId => _config["Facebook:AppId"] ?? throw new InvalidOperationException("Facebook:AppId not set in appsettings.json");
        private string AppSecret => _config["Facebook:AppSecret"] ?? throw new InvalidOperationException("Facebook:AppSecret not set in appsettings.json");

        string RedirectUri = "https://localhost:7276/Integrations/Callback/facebook";

        //private string RedirectUri => _config["Facebook:RedirectUri"] ?? throw new InvalidOperationException("Facebook:RedirectUri not set in appsettings.json");

        private static readonly string[] Scopes =
        {
            "pages_manage_posts",
            "pages_read_engagement",
            "pages_manage_comments",
            "ads_management"
        };

        string scope = "public_profile,email,pages_show_list,pages_manage_metadata,pages_read_engagement,pages_manage_engagement,pages_manage_posts,pages_read_user_content,leads_retrieval";


        // ── Step 1: Build OAuth URL ──────────────────────────────────
        public string BuildOAuthUrl(string state)
        {
            var scopeStr = string.Join(",", scope);
            return "https://www.facebook.com/v19.0/dialog/oauth"
                 + $"?client_id={Uri.EscapeDataString(AppId)}"
                 + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
                 + $"&scope={Uri.EscapeDataString(scopeStr)}"
                 + $"&state={Uri.EscapeDataString(state)}"
                 + "&response_type=code";
        }

        // ── Step 2: Exchange auth code for tokens ────────────────────
        public async Task<FacebookTokenResult> ExchangeCodeAsync(string code)
        {
            var url = "https://graph.facebook.com/v19.0/oauth/access_token"
                    + $"?client_id={Uri.EscapeDataString(AppId)}"
                    + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
                    + $"&client_secret={Uri.EscapeDataString(AppSecret)}"
                    + $"&code={Uri.EscapeDataString(code)}";

            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var payload = System.Text.Json.JsonSerializer.Deserialize<FbTokenPayload>(json);

            if (payload?.AccessToken == null)
                throw new InvalidOperationException("Facebook did not return an access token.");

            var longLived = await _GetLongLivedTokenAsync(payload.AccessToken);

            return new FacebookTokenResult
            {
                ShortLivedToken = payload.AccessToken,
                LongLivedToken = longLived,
                ExpiresInSeconds = payload.ExpiresIn
            };
        }

        private async Task<string> _GetLongLivedTokenAsync(string shortLived)
        {
            var url = "https://graph.facebook.com/v19.0/oauth/access_token"
                    + $"?grant_type=fb_exchange_token"
                    + $"&client_id={Uri.EscapeDataString(AppId)}"
                    + $"&client_secret={Uri.EscapeDataString(AppSecret)}"
                    + $"&fb_exchange_token={Uri.EscapeDataString(shortLived)}";

            var r = await _http.GetAsync(url);
            r.EnsureSuccessStatusCode();
            var p = System.Text.Json.JsonSerializer.Deserialize<FbTokenPayload>(
                        await r.Content.ReadAsStringAsync());
            return p?.AccessToken ?? throw new InvalidOperationException("Long-lived token exchange failed.");
        }

        // ── Step 3: Get managed Pages ────────────────────────────────
        public async Task<List<FacebookPage>> GetManagedPagesAsync(string userAccessToken)
        {
            var url = "https://graph.facebook.com/v19.0/me/accounts"
                    + $"?access_token={Uri.EscapeDataString(userAccessToken)}"
                    + "&fields=id,name,access_token,category";

            var r = await _http.GetAsync(url);
            r.EnsureSuccessStatusCode();
            var root = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

            var pages = new List<FacebookPage>();
            if (root.TryGetProperty("data", out var data))
                foreach (var item in data.EnumerateArray())
                    pages.Add(new FacebookPage
                    {
                        PageId = item.GetProperty("id").GetString() ?? "",
                        Name = item.GetProperty("name").GetString() ?? "",
                        AccessToken = item.GetProperty("access_token").GetString() ?? "",
                        Category = item.TryGetProperty("category", out var cat) ? cat.GetString() ?? "" : ""
                    });
            return pages;
        }

        // ── Disconnect: revoke app permissions ───────────────────────
        public async Task<bool> DisconnectAsync(string accessToken)
        {
            var r = await _http.DeleteAsync(
                $"https://graph.facebook.com/v19.0/me/permissions?access_token={Uri.EscapeDataString(accessToken)}");
            return r.IsSuccessStatusCode;
        }
    }

    // Supporting types
    public class FacebookTokenResult
    {
        public string ShortLivedToken { get; set; } = "";
        public string LongLivedToken { get; set; } = "";
        public int ExpiresInSeconds { get; set; }
    }

    public class FacebookPage
    {
        public string PageId { get; set; } = "";
        public string Name { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string Category { get; set; } = "";
    }

    internal class FbTokenPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}