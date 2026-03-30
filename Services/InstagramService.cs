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

        private string AppId => _config["Facebook:AppId"]!;
        private string AppSecret => _config["Facebook:AppSecret"]!;
        private string RedirectUri => "https://localhost:7276/Integrations/Callback/instagram";

        // ✅ Instagram scopes (IMPORTANT)
        private string scope =
            "instagram_basic,instagram_manage_comments,instagram_manage_insights,pages_show_list,pages_read_engagement";

        // ── Step 1: OAuth URL ───────────────────────────────
        public string BuildOAuthUrl(string state)
        {
            return "https://www.facebook.com/v19.0/dialog/oauth"
                + $"?client_id={Uri.EscapeDataString(AppId)}"
                + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
                + $"&scope={Uri.EscapeDataString(scope)}"
                + $"&state={Uri.EscapeDataString(state)}"
                + "&response_type=code";
        }

        // ── Step 2: Exchange Token (same as FB) ─────────────
        public async Task<string> ExchangeCodeAsync(string code)
        {
            var url = "https://graph.facebook.com/v19.0/oauth/access_token"
                + $"?client_id={AppId}"
                + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
                + $"&client_secret={AppSecret}"
                + $"&code={code}";

            var res = await _http.GetAsync(url);
            res.EnsureSuccessStatusCode();

            var json = await res.Content.ReadAsStringAsync();
            var payload = JsonSerializer.Deserialize<FbTokenPayloadInsta>(json);

            return payload?.AccessToken ?? throw new Exception("Token failed");
        }

        // ── Step 3: Get Instagram Business Account ─────────
        public async Task<List<InstagramAccount>> GetInstagramAccountsAsync(string userToken)
        {
            // Step 1: get pages
            var pagesUrl = $"https://graph.facebook.com/v19.0/me/accounts?access_token={userToken}";
            var pagesRes = await _http.GetAsync(pagesUrl);
            pagesRes.EnsureSuccessStatusCode();

            var pagesJson = JsonDocument.Parse(await pagesRes.Content.ReadAsStringAsync());

            var result = new List<InstagramAccount>();

            foreach (var page in pagesJson.RootElement.GetProperty("data").EnumerateArray())
            {
                var pageId = page.GetProperty("id").GetString();
                var pageToken = page.GetProperty("access_token").GetString();

                // Step 2: get IG account from page
                var igUrl = $"https://graph.facebook.com/v19.0/{pageId}?fields=instagram_business_account&access_token={pageToken}";
                var igRes = await _http.GetAsync(igUrl);
                igRes.EnsureSuccessStatusCode();

                var igJson = JsonDocument.Parse(await igRes.Content.ReadAsStringAsync());

                if (igJson.RootElement.TryGetProperty("instagram_business_account", out var igAcc))
                {
                    var igId = igAcc.GetProperty("id").GetString();

                    // Step 3: get IG details
                    var detailUrl = $"https://graph.facebook.com/v19.0/{igId}?fields=username,profile_picture_url&access_token={pageToken}";
                    var detailRes = await _http.GetAsync(detailUrl);
                    detailRes.EnsureSuccessStatusCode();

                    var detailJson = JsonDocument.Parse(await detailRes.Content.ReadAsStringAsync());

                    result.Add(new InstagramAccount
                    {
                        InstagramId = igId!,
                        Username = detailJson.RootElement.GetProperty("username").GetString() ?? "",
                        ProfilePicture = detailJson.RootElement.GetProperty("profile_picture_url").GetString() ?? "",
                        PageId = pageId!,
                        PageAccessToken = pageToken!
                    });
                }
            }

            return result;
        }

        // ── Reply to comment ───────────────────────────────
        public async Task<(bool Success, string Error)> ReplyToCommentAsync(
            string commentId, string message, string pageToken)
        {
            var url = $"https://graph.facebook.com/v19.0/{commentId}/replies";

            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("message", message),
                new KeyValuePair<string,string>("access_token", pageToken)
            });

            var res = await _http.PostAsync(url, body);
            var json = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                return (false, json);

            return (true, "");
        }
    }

    public class InstagramAccount
    {
        public string InstagramId { get; set; } = "";
        public string Username { get; set; } = "";
        public string ProfilePicture { get; set; } = "";
        public string PageId { get; set; } = "";
        public string PageAccessToken { get; set; } = "";
    }

    internal class FbTokenPayloadInsta
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
    }
}