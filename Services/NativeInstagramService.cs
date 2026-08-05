using System.Text;
using System.Text.Json;

namespace SocialMediaPanel.Services
{
    /// <summary>
    /// The direct "Instagram API with Instagram Login" flow — a separate,
    /// parallel connect path from the existing Facebook-Login-based Instagram
    /// integration in InstagramService. This one needs no linked Facebook
    /// Page: the Instagram Business/Creator account authorizes the app
    /// directly, and every call afterward goes through graph.instagram.com
    /// using that account's own token (stored on InstagramAccount.NativeAccessToken),
    /// not a Page access token.
    /// </summary>
    public class NativeInstagramService
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpFactory;

        public NativeInstagramService(IConfiguration config, IHttpClientFactory httpFactory)
        {
            _config = config;
            _httpFactory = httpFactory;
        }

        private string AppId => _config["Instagram:AppId"]!;
        private string AppSecret => _config["Instagram:AppSecret"]!;
        private string RedirectUri => _config["Instagram:NativeRedirectUri"]
            ?? throw new InvalidOperationException("Instagram:NativeRedirectUri not set in appsettings.json");

        // New-style permissions for the native login product — distinct names
        // from the Facebook-Login-based instagram_basic/instagram_manage_*.
        public const string Scope =
            "instagram_business_basic,instagram_business_content_publish," +
            "instagram_business_manage_comments,instagram_business_manage_messages," +
            "instagram_business_manage_insights";

        // ── Step 1: authorize URL ───────────────────────────────────────
        public string BuildOAuthUrl(string state)
        {
            return "https://www.instagram.com/oauth/authorize" +
                   $"?client_id={Uri.EscapeDataString(AppId)}" +
                   $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                   $"&scope={Uri.EscapeDataString(Scope)}" +
                   $"&state={Uri.EscapeDataString(state)}" +
                   "&response_type=code";
        }

        // ── Step 2: exchange code for a short-lived token ────────────────
        public async Task<(string AccessToken, string IgUserId)> ExchangeCodeAsync(string code)
        {
            var client = _httpFactory.CreateClient();
            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("client_id", AppId),
                new KeyValuePair<string,string>("client_secret", AppSecret),
                new KeyValuePair<string,string>("grant_type", "authorization_code"),
                new KeyValuePair<string,string>("redirect_uri", RedirectUri),
                new KeyValuePair<string,string>("code", code)
            });

            var res = await client.PostAsync("https://api.instagram.com/oauth/access_token", body);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new Exception($"Instagram native token exchange failed: {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var token = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            var userId = root.TryGetProperty("user_id", out var uid)
                ? uid.ValueKind == JsonValueKind.Number ? uid.GetInt64().ToString() : uid.GetString()
                : null;

            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(userId))
                throw new Exception($"Instagram native token exchange returned no token/user_id: {json}");

            return (token, userId);
        }

        // ── Step 3: exchange for a 60-day long-lived token ───────────────
        public async Task<string> GetLongLivedTokenAsync(string shortLivedToken)
        {
            var client = _httpFactory.CreateClient();
            var url = "https://graph.instagram.com/access_token" +
                       "?grant_type=ig_exchange_token" +
                       $"&client_secret={Uri.EscapeDataString(AppSecret)}" +
                       $"&access_token={Uri.EscapeDataString(shortLivedToken)}";

            try
            {
                var json = await client.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("access_token", out var at)
                    ? at.GetString() ?? shortLivedToken
                    : shortLivedToken;
            }
            catch
            {
                return shortLivedToken; // fall back to the short-lived one rather than fail the connect flow
            }
        }

        // ── Profile info ─────────────────────────────────────────────────
        public async Task<(string Username, string AccountType, string ProfilePictureUrl)> GetProfileAsync(string accessToken)
        {
            var client = _httpFactory.CreateClient();
            var url = $"https://graph.instagram.com/v21.0/me?fields=username,account_type,profile_picture_url&access_token={Uri.EscapeDataString(accessToken)}";
            var json = await client.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var username = root.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
            var accountType = root.TryGetProperty("account_type", out var t) ? t.GetString() ?? "" : "";
            var pic = root.TryGetProperty("profile_picture_url", out var p) ? p.GetString() ?? "" : "";
            return (username, accountType, pic);
        }

        // ── Publish a photo post (2-step: create container, then publish) ─
        public async Task<(bool Success, string PostIdOrError)> PublishPhotoAsync(
            string igUserId, string accessToken, string imageUrl, string? caption)
        {
            var client = _httpFactory.CreateClient();
            try
            {
                var createUrl = $"https://graph.instagram.com/v21.0/{igUserId}/media";
                var createBody = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string,string>("image_url", imageUrl),
                    new KeyValuePair<string,string>("caption", caption ?? ""),
                    new KeyValuePair<string,string>("access_token", accessToken)
                });
                var createRes = await client.PostAsync(createUrl, createBody);
                var createJson = await createRes.Content.ReadAsStringAsync();
                if (!createRes.IsSuccessStatusCode)
                    return (false, $"Container create failed: {createJson}");

                using var createDoc = JsonDocument.Parse(createJson);
                var creationId = createDoc.RootElement.TryGetProperty("id", out var cid) ? cid.GetString() : null;
                if (string.IsNullOrEmpty(creationId))
                    return (false, $"No creation id returned: {createJson}");

                var publishUrl = $"https://graph.instagram.com/v21.0/{igUserId}/media_publish";
                var publishBody = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string,string>("creation_id", creationId),
                    new KeyValuePair<string,string>("access_token", accessToken)
                });
                var publishRes = await client.PostAsync(publishUrl, publishBody);
                var publishJson = await publishRes.Content.ReadAsStringAsync();
                if (!publishRes.IsSuccessStatusCode)
                    return (false, $"Publish failed: {publishJson}");

                using var publishDoc = JsonDocument.Parse(publishJson);
                var postId = publishDoc.RootElement.TryGetProperty("id", out var pid) ? pid.GetString() : null;
                return (true, postId ?? "");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // ── Recent media + comments ───────────────────────────────────────
        public async Task<string> GetRecentMediaAsync(string igUserId, string accessToken, int limit = 10)
        {
            var client = _httpFactory.CreateClient();
            var url = $"https://graph.instagram.com/v21.0/{igUserId}/media" +
                      $"?fields=id,caption,media_url,permalink,timestamp,like_count,comments_count" +
                      $"&limit={limit}&access_token={Uri.EscapeDataString(accessToken)}";
            return await client.GetStringAsync(url);
        }

        public async Task<string> GetMediaCommentsAsync(string mediaId, string accessToken)
        {
            var client = _httpFactory.CreateClient();
            var url = $"https://graph.instagram.com/v21.0/{mediaId}/comments" +
                      $"?fields=id,text,username,timestamp&access_token={Uri.EscapeDataString(accessToken)}";
            return await client.GetStringAsync(url);
        }

        public async Task<(bool Success, string Error)> ReplyToCommentAsync(string commentId, string message, string accessToken)
        {
            var client = _httpFactory.CreateClient();
            var url = $"https://graph.instagram.com/v21.0/{commentId}/replies";
            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("message", message),
                new KeyValuePair<string,string>("access_token", accessToken)
            });
            var res = await client.PostAsync(url, body);
            var json = await res.Content.ReadAsStringAsync();
            return res.IsSuccessStatusCode ? (true, "") : (false, json);
        }

        public async Task<(bool Success, string Error)> HideCommentAsync(string commentId, bool hide, string accessToken)
        {
            var client = _httpFactory.CreateClient();
            var url = $"https://graph.instagram.com/v21.0/{commentId}?hide={(hide ? "true" : "false")}&access_token={Uri.EscapeDataString(accessToken)}";
            var res = await client.PostAsync(url, null);
            var json = await res.Content.ReadAsStringAsync();
            return res.IsSuccessStatusCode ? (true, "") : (false, json);
        }

        public async Task<(bool Success, string Error)> DeleteCommentAsync(string commentId, string accessToken)
        {
            var client = _httpFactory.CreateClient();
            var url = $"https://graph.instagram.com/v21.0/{commentId}?access_token={Uri.EscapeDataString(accessToken)}";
            var res = await client.DeleteAsync(url);
            var json = await res.Content.ReadAsStringAsync();
            return res.IsSuccessStatusCode ? (true, "") : (false, json);
        }

        // ── Direct message ────────────────────────────────────────────────
        // Native-login messaging uses the Authorization header + graph.instagram.com,
        // confirmed against Meta's own "API Integration Helper" sample for this product.
        public async Task<(bool Success, string Error)> SendDMAsync(string recipientId, string message, string accessToken)
        {
            var client = _httpFactory.CreateClient();
            var payload = new
            {
                recipient = new { id = recipientId },
                message = new { text = message }
            };
            var req = new HttpRequestMessage(HttpMethod.Post, "https://graph.instagram.com/v21.0/me/messages")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Authorization", $"Bearer {accessToken}");

            var res = await client.SendAsync(req);
            var json = await res.Content.ReadAsStringAsync();
            return res.IsSuccessStatusCode ? (true, "") : (false, json);
        }

        // ── Account insights ──────────────────────────────────────────────
        public async Task<(int Reach, int ProfileViews, int Followers)> GetInsightsAsync(string igUserId, string accessToken)
        {
            var client = _httpFactory.CreateClient();
            int reach = 0, profileViews = 0, followers = 0;

            try
            {
                var url = $"https://graph.instagram.com/v21.0/{igUserId}/insights" +
                          $"?metric=reach,profile_views&period=day&metric_type=total_value&access_token={Uri.EscapeDataString(accessToken)}";
                var body = await client.GetStringAsync(url);
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

                        if (name == "reach") reach = val;
                        else if (name == "profile_views") profileViews = val;
                    }
                }
            }
            catch { /* metric may be unavailable */ }

            try
            {
                var acctUrl = $"https://graph.instagram.com/v21.0/{igUserId}?fields=followers_count&access_token={Uri.EscapeDataString(accessToken)}";
                var acctBody = await client.GetStringAsync(acctUrl);
                using var acctDoc = JsonDocument.Parse(acctBody);
                if (acctDoc.RootElement.TryGetProperty("followers_count", out var fc) && fc.ValueKind == JsonValueKind.Number)
                    followers = fc.GetInt32();
            }
            catch { /* optional */ }

            return (reach, profileViews, followers);
        }
    }
}
