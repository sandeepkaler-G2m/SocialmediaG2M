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

        // Read from appsettings.json (Facebook:RedirectUri) so switching between
        // local testing and production is a config change, not a code edit —
        // must exactly match a "Valid OAuth Redirect URI" in the Meta App dashboard.
        private string RedirectUri => _config["Facebook:RedirectUri"]
            ?? throw new InvalidOperationException("Facebook:RedirectUri not set in appsettings.json");

        // ── Step 1: Build OAuth URL ──────────────────────────────────
        public string BuildOAuthUrl(string state)
        {
            var scopeStr = MetaScopes.Full;
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

        // ── Page-level insights: views/engagement + fan count ─────────────
        // Requires pages_read_engagement. Wrapped defensively — some page
        // metrics get deprecated/renamed across Graph API versions, so a
        // failure here just leaves the numbers at 0 instead of breaking Reports.
        //
        // Confirmed live against the current API version: page_impressions,
        // page_impressions_unique, page_engaged_users and page_fan_adds are all
        // now rejected ("The value must be a valid insights metric"). Meta has
        // been steadily retiring Page-level impression/engagement metrics;
        // page_views_total, page_post_engagements and page_follows are the
        // current replacements that still validate.
        public async Task<(int Impressions, int ImpressionsUnique, int EngagedUsers, int Fans)> GetPageInsightsAsync(
            string pageId, string pageToken)
        {
            int pageViews = 0, postEngagements = 0, follows = 0, fans = 0;

            try
            {
                var url = $"https://graph.facebook.com/v19.0/{pageId}/insights" +
                          $"?metric=page_views_total,page_post_engagements,page_follows" +
                          $"&period=days_28&access_token={Uri.EscapeDataString(pageToken)}";
                var body = await _http.GetStringAsync(url);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var metric in data.EnumerateArray())
                    {
                        var name = metric.TryGetProperty("name", out var n) ? n.GetString() : "";
                        int sum = 0;
                        if (metric.TryGetProperty("values", out var values))
                        {
                            foreach (var v in values.EnumerateArray())
                                if (v.TryGetProperty("value", out var val) && val.ValueKind == System.Text.Json.JsonValueKind.Number)
                                    sum += val.GetInt32();
                        }
                        switch (name)
                        {
                            case "page_views_total": pageViews = sum; break;
                            case "page_post_engagements": postEngagements = sum; break;
                            case "page_follows": follows = sum; break;
                        }
                    }
                }
            }
            catch { /* metric may be unavailable for this API version/page */ }

            try
            {
                var fanUrl = $"https://graph.facebook.com/v19.0/{pageId}?fields=fan_count&access_token={Uri.EscapeDataString(pageToken)}";
                var fanBody = await _http.GetStringAsync(fanUrl);
                using var fanDoc = System.Text.Json.JsonDocument.Parse(fanBody);
                if (fanDoc.RootElement.TryGetProperty("fan_count", out var fc) && fc.ValueKind == System.Text.Json.JsonValueKind.Number)
                    fans = fc.GetInt32();
            }
            catch { /* optional */ }

            return (pageViews, follows, postEngagements, fans);
        }

        // ── Lead forms + leads (leads_retrieval) — manual pull fallback ────
        public async Task<List<(string FormId, string Name)>> GetLeadFormsAsync(string pageId, string pageToken)
        {
            var forms = new List<(string, string)>();
            try
            {
                var url = $"https://graph.facebook.com/v19.0/{pageId}/leadgen_forms" +
                          $"?fields=id,name&access_token={Uri.EscapeDataString(pageToken)}";
                var body = await _http.GetStringAsync(url);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var f in data.EnumerateArray())
                    {
                        var id = f.TryGetProperty("id", out var idp) ? idp.GetString() : null;
                        var name = f.TryGetProperty("name", out var np) ? np.GetString() : "";
                        if (!string.IsNullOrEmpty(id)) forms.Add((id, name ?? ""));
                    }
                }
            }
            catch { /* no forms / permission missing */ }
            return forms;
        }

        public async Task<List<(string LeadId, string RawJson)>> GetFormLeadsAsync(string formId, string pageToken)
        {
            var leads = new List<(string, string)>();
            try
            {
                var url = $"https://graph.facebook.com/v19.0/{formId}/leads" +
                          $"?fields=id,field_data,created_time&access_token={Uri.EscapeDataString(pageToken)}";
                var body = await _http.GetStringAsync(url);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var l in data.EnumerateArray())
                    {
                        var id = l.TryGetProperty("id", out var idp) ? idp.GetString() : null;
                        if (!string.IsNullOrEmpty(id)) leads.Add((id, l.GetRawText()));
                    }
                }
            }
            catch { /* no leads / permission missing */ }
            return leads;
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