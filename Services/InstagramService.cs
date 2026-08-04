using Azure.Core;
using LinqToTwitter.OAuth;
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
        private string fbAppId => _config["Facebook:AppId"];
        private string fbAppSecret => _config["Facebook:AppSecret"] ?? throw new InvalidOperationException("Facebook:AppSecret not set in appsettings.json");


        private string AppSecret => _config["Instagram:AppSecret"]!;
        // Read from appsettings.json (Instagram:RedirectUri) — same reasoning as
        // FacebookService.RedirectUri.
        private string RedirectUri => _config["Instagram:RedirectUri"]
            ?? throw new InvalidOperationException("Instagram:RedirectUri not set in appsettings.json");

        // Shared full scope (see MetaScopes) — same permission set as the Facebook
        // connect button, so connecting via Instagram also grants posting/ads/leads.
        private string Scope = MetaScopes.Full;
        // ── Step 1: OAuth URL ───────────────────────────────
        public string BuildOAuthUrl(string state)
        {


            // ✅ www.instagram.com NOT api.instagram.com
            //return "https://www.instagram.com/oauth/authorize"
            //    + $"?client_id={Uri.EscapeDataString(AppId)}"
            //    + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            //    + $"&scope={Uri.EscapeDataString(Scope)}"
            //    + $"&state={Uri.EscapeDataString(state)}"
            //    + "&response_type=code"
            //    + "&force_reauth=true";  // optional but good UX


            return $"https://www.facebook.com/v19.0/dialog/oauth" +
       $"?client_id={Uri.EscapeDataString(fbAppId)}" +
       $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
       $"&scope={Uri.EscapeDataString(Scope)}" +
       $"&state={Uri.EscapeDataString(state)}" +
       $"&response_type=code";
        }

        // ── Step 2: Exchange Token (same as FB) ─────────────
        public async Task<string> ExchangeCodeAsync(string code)
        {
            var body = new FormUrlEncodedContent(new[]
            {
        new KeyValuePair<string,string>("client_id",     fbAppId),      // Facebook App ID
        new KeyValuePair<string,string>("client_secret", fbAppSecret),  // Facebook App Secret
        new KeyValuePair<string,string>("grant_type",    "authorization_code"),
        new KeyValuePair<string,string>("redirect_uri",  RedirectUri),
        new KeyValuePair<string,string>("code",          code)
    });

            var res = await _http.PostAsync("https://graph.facebook.com/v19.0/oauth/access_token", body);
            var json = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                throw new Exception($"Token exchange failed: {json}");

            using var doc = JsonDocument.Parse(json);
            var accessToken = doc.RootElement
                .TryGetProperty("access_token", out var at) ? at.GetString() : null;

            return accessToken ?? throw new Exception("No access_token in response");
        }

        // ── Step 3: Exchange for long-lived token (60 days) ─
        // ── Step 3: Get long-lived token — with fallback ───
        public async Task<string> GetLongLivedTokenAsync(string shortLivedToken)
        {
            try
            {
                var url = "https://graph.facebook.com/v19.0/oauth/access_token"
                    + $"?grant_type=fb_exchange_token"
                    + $"&client_id={fbAppId}"
                    + $"&client_secret={fbAppSecret}"
                    + $"&fb_exchange_token={shortLivedToken}";

                var res = await _http.GetAsync(url);
                var json = await res.Content.ReadAsStringAsync();

                Console.WriteLine("LONG TOKEN RESPONSE: " + json);

                if (!res.IsSuccessStatusCode)
                {
                    Console.WriteLine("Long token failed, using short token as fallback");
                    return shortLivedToken;
                }

                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("access_token", out var at)
                    ? at.GetString() ?? shortLivedToken
                    : shortLivedToken;
            }
            catch (Exception ex)
            {
                Console.WriteLine("GetLongLivedToken error: " + ex.Message);
                return shortLivedToken; // fallback
            }
        }
       
        // ── Reply to comment ───────────────────────────────
        public async Task<(bool Success, string Error)> ReplyToCommentAsync(
            string commentId, string message, string accessToken)
        {
            // Now uses graph.instagram.com
            var url = $"https://graph.facebook.com/v19.0/{commentId}/replies";

            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("message", message),
                new KeyValuePair<string,string>("access_token", accessToken)
            });

            var res = await _http.PostAsync(url, body);
            var json = await res.Content.ReadAsStringAsync();

            return res.IsSuccessStatusCode ? (true, "") : (false, json);
        }

        // ── Account-level insights: reach/profile views + followers ──
        // Requires instagram_manage_insights. Defensive — leaves 0s on failure
        // rather than breaking the Reports page.
        //
        // Confirmed live against the current API version: "impressions" isn't
        // in Meta's valid account-metric list any more, and "profile_views"
        // (like several other account metrics) errors unless requested with
        // metric_type=total_value, which returns a "total_value":{"value":N}
        // shape instead of the "values":[{"value":N}] array used elsewhere.
        public async Task<(int Reach, int Impressions, int ProfileViews, int Followers)> GetAccountInsightsAsync(
            string igUserId, string pageToken)
        {
            int reach = 0, profileViews = 0, followers = 0;

            try
            {
                var url = $"https://graph.facebook.com/v19.0/{igUserId}/insights" +
                          $"?metric=reach,profile_views&period=day&metric_type=total_value&access_token={Uri.EscapeDataString(pageToken)}";
                var body = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var metric in data.EnumerateArray())
                    {
                        var name = metric.TryGetProperty("name", out var n) ? n.GetString() : "";
                        int val = 0;
                        if (metric.TryGetProperty("total_value", out var tv) &&
                            tv.TryGetProperty("value", out var tvv) && tvv.ValueKind == JsonValueKind.Number)
                            val = tvv.GetInt32();

                        switch (name)
                        {
                            case "reach": reach = val; break;
                            case "profile_views": profileViews = val; break;
                        }
                    }
                }
            }
            catch { /* metric may be unavailable for this API version */ }

            try
            {
                var acctUrl = $"https://graph.facebook.com/v19.0/{igUserId}?fields=followers_count&access_token={Uri.EscapeDataString(pageToken)}";
                var acctBody = await _http.GetStringAsync(acctUrl);
                using var acctDoc = JsonDocument.Parse(acctBody);
                if (acctDoc.RootElement.TryGetProperty("followers_count", out var fc) && fc.ValueKind == JsonValueKind.Number)
                    followers = fc.GetInt32();
            }
            catch { /* optional */ }

            return (reach, 0, profileViews, followers);
        }
    }

    // Unused OAuth-exchange DTO (kept for reference only — not the DB model).
    // Renamed to avoid colliding with the real, DB-backed
    // SocialMediaPanel.Controllers.InstagramAccount used everywhere else.
    public class InstagramOAuthAccountInfo
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