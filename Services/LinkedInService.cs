using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SocialMediaPanel.Services
{
    public class LinkedInService
    {
        private readonly HttpClient _http;
        private readonly IConfiguration _config;

        public LinkedInService(HttpClient http, IConfiguration config)
        {
            _http = http;
            _config = config;
        }

        // ── OAuth helpers ─────────────────────────────────────────────

        public string BuildOAuthUrl(string state)
        {
            var clientId = _config["LinkedIn:ClientId"] ?? throw new Exception("LinkedIn:ClientId not set");
            var redirectUri = _config["LinkedIn:RedirectUri"] ?? throw new Exception("LinkedIn:RedirectUri not set");

            var scopes = "openid profile email w_member_social";

            return "https://www.linkedin.com/oauth/v2/authorization"
                 + $"?response_type=code"
                 + $"&client_id={Uri.EscapeDataString(clientId)}"
                 + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                 + $"&state={Uri.EscapeDataString(state)}"
                 + $"&scope={Uri.EscapeDataString(scopes)}";
        }

        public async Task<(string AccessToken, int ExpiresIn, string? RefreshToken)> ExchangeCodeAsync(string code)
        {
            var clientId = _config["LinkedIn:ClientId"]!;
            var clientSecret = _config["LinkedIn:ClientSecret"]!;
            var redirectUri = _config["LinkedIn:RedirectUri"]!;

            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type",    "authorization_code" },
                { "code",          code                 },
                { "redirect_uri",  redirectUri          },
                { "client_id",     clientId             },
                { "client_secret", clientSecret         }
            });

            var resp = await _http.PostAsync("https://www.linkedin.com/oauth/v2/accessToken", body);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn token exchange failed: {json}");

            using var doc = JsonDocument.Parse(json);
            var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

            return (accessToken, expiresIn, refreshToken);
        }

        public async Task<(string Id, string Name, string? Picture, string? Email)> GetProfileAsync(string accessToken)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // OpenID userinfo endpoint (works with openid+profile+email scopes)
            var resp = await _http.GetAsync("https://api.linkedin.com/v2/userinfo");
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn profile fetch failed: {json}");

            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.TryGetProperty("sub", out var s) ? s.GetString() ?? "" : "";
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var picture = doc.RootElement.TryGetProperty("picture", out var p) ? p.GetString() : null;
            var email = doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;

            return (id, name, picture, email);
        }

        // ── Posting ──────────────────────────────────────────────────

        /// <summary>Posts a text post (with optional article link or image) to LinkedIn.</summary>
        public async Task<string> PostAsync(
            string accessToken,
            string linkedInUserId,
            string text,
            string? articleUrl = null,
            string? imageBase64 = null,
            string? imageMime = null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // Build the share content
            object shareContent;

            if (!string.IsNullOrEmpty(articleUrl))
            {
                shareContent = new
                {
                    shareCommentary = new { text },
                    shareMediaCategory = "ARTICLE",
                    media = new[] { new { status = "READY", originalUrl = articleUrl } }
                };
            }
            else if (!string.IsNullOrEmpty(imageBase64) && !string.IsNullOrEmpty(imageMime))
            {
                // Step 1: register upload
                var asset = await _RegisterImageUploadAsync(accessToken, linkedInUserId);

                // Step 2: upload binary
                await _UploadImageAsync(asset.UploadUrl, imageBase64, imageMime);

                shareContent = new
                {
                    shareCommentary = new { text },
                    shareMediaCategory = "IMAGE",
                    media = new[]
                    {
                        new { status = "READY", media = asset.Asset }
                    }
                };
            }
            else
            {
                shareContent = new
                {
                    shareCommentary = new { text },
                    shareMediaCategory = "NONE"
                };
            }

            var payloadObj = new
            {
                author = $"urn:li:person:{linkedInUserId}",
                lifecycleState = "PUBLISHED",
                specificContent = new Dictionary<string, object>
    {
        { "com.linkedin.ugc.ShareContent", shareContent }
    },
                visibility = new Dictionary<string, string>
    {
        { "com.linkedin.ugc.MemberNetworkVisibility", "PUBLIC" }
    }
            };

            var payload = JsonSerializer.Serialize(payloadObj);

            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.linkedin.com/v2/ugcPosts")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-Restli-Protocol-Version", "2.0.0");

            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn post failed ({(int)resp.StatusCode}): {json}");

            // Return the post URN from X-RestLi-Id header or parse from response
            if (resp.Headers.TryGetValues("X-RestLi-Id", out var vals))
                return vals.FirstOrDefault() ?? "";

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
        }

        // ── Image upload helpers ──────────────────────────────────────

        private async Task<(string UploadUrl, string Asset)> _RegisterImageUploadAsync(
            string accessToken, string linkedInUserId)
        {
            var payload = JsonSerializer.Serialize(new
            {
                registerUploadRequest = new
                {
                    recipes = new[] { "urn:li:digitalmediaRecipe:feedshare-image" },
                    owner = $"urn:li:person:{linkedInUserId}",
                    serviceRelationships = new[]
                    {
                        new { relationshipType = "OWNER", identifier = "urn:li:userGeneratedContent" }
                    }
                }
            });

            var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.linkedin.com/v2/assets?action=registerUpload")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Image registration failed: {json}");

            using var doc = JsonDocument.Parse(json);
            var value = doc.RootElement.GetProperty("value");
            var uploadUrl = value.GetProperty("uploadMechanism")
                                   .GetProperty("com.linkedin.digitalmedia.uploading.MediaUploadHttpRequest")
                                   .GetProperty("uploadUrl").GetString()!;
            var asset = value.GetProperty("asset").GetString()!;

            return (uploadUrl, asset);
        }

        private async Task _UploadImageAsync(string uploadUrl, string base64Data, string mimeType)
        {
            var bytes = Convert.FromBase64String(base64Data);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
            await _http.PutAsync(uploadUrl, content);
        }

        // ── Token refresh ─────────────────────────────────────────────

        public async Task<(string AccessToken, int ExpiresIn)> RefreshTokenAsync(string refreshToken)
        {
            var clientId = _config["LinkedIn:ClientId"]!;
            var clientSecret = _config["LinkedIn:ClientSecret"]!;

            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type",    "refresh_token" },
                { "refresh_token", refreshToken    },
                { "client_id",     clientId        },
                { "client_secret", clientSecret    }
            });

            var resp = await _http.PostAsync("https://www.linkedin.com/oauth/v2/accessToken", body);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn token refresh failed: {json}");

            using var doc = JsonDocument.Parse(json);
            var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

            return (accessToken, expiresIn);
        }
    }
}