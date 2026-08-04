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

        // Only the scopes granted by LinkedIn's two self-serve, auto-approved
        // products are requested here — "Sign In with LinkedIn using OpenID
        // Connect" (openid, profile, email) and "Share on LinkedIn"
        // (w_member_social). r_liteprofile/r_profile_basicinfo/r_verify were
        // never valid current scope names (LinkedIn migrated profile access
        // to OpenID Connect years ago), and the organization/ads/leadgen
        // scopes below all require LinkedIn to separately approve this app
        // for a restricted product (Community Management API for
        // organization posting, Marketing Developer Platform for ads,
        // Lead Sync API for lead forms) — requesting them before that
        // approval risks LinkedIn rejecting the whole authorization request,
        // the same way Facebook rejected an unrecognized scope name.
        // Add them back here ONLY after the corresponding product shows
        // "Approved" (not just "Requested") in the LinkedIn Developer Portal.
        private const string RestrictedScopesPendingApproval =
            "r_organization_social w_organization_social rw_organization_admin " // Community Management API
          + "r_ads rw_ads r_ads_reporting "                                       // Marketing Developer Platform (Advertising)
          + "r_marketing_leadgen_automation";                                     // Lead Sync API

        public string BuildOAuthUrl(string state)
        {
            var clientId = _config["LinkedIn:ClientId"];
            var redirectUri = _config["LinkedIn:RedirectUri"];

            var scopes = "openid profile email w_member_social";

            return "https://www.linkedin.com/oauth/v2/authorization"
                + "?response_type=code"
                + "&client_id=" + Uri.EscapeDataString(clientId)
                + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
                + "&scope=" + Uri.EscapeDataString(scopes)
                + "&state=" + Uri.EscapeDataString(state);
        }

        public async Task<(string AccessToken, int ExpiresIn, string? RefreshToken, string? GrantedScopes)> ExchangeCodeAsync(string code)
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
            var grantedScopes = doc.RootElement.TryGetProperty("scope", out var sc) ? sc.GetString() : null;

            return (accessToken, expiresIn, refreshToken, grantedScopes);
        }

        public async Task<(string Id, string Name, string? Picture, string? Email)> GetProfileAsync(string accessToken)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

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

        // ── Organization pages (needs r_organization_admin) ──
        public async Task<List<(string Urn, string Name, string? LogoUrl)>> GetOrganizationsAsync(string accessToken)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var resp = await _http.GetAsync(
                "https://api.linkedin.com/v2/organizationalEntityAcls?q=roleAssignee");
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn organizations fetch failed: {json}");

            var results = new List<(string, string, string?)>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("elements", out var elements))
            {
                foreach (var el in elements.EnumerateArray())
                {
                    var orgUrn = el.TryGetProperty("organizationalTarget", out var t) ? t.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(orgUrn)) continue;
                    results.Add((orgUrn, orgUrn, null)); // name/logo needs a follow-up call per org id
                }
            }
            return results;
        }

        // ── Lead Gen Forms leads (needs r_marketing_leadgen_automation / r_ads_leadgen_automation) ──
        public async Task<List<Dictionary<string, JsonElement>>> GetLeadsAsync(string accessToken, string organizationUrn)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var url = $"https://api.linkedin.com/v2/leadFormResponses?q=owner&owner={Uri.EscapeDataString(organizationUrn)}";
            var resp = await _http.GetAsync(url);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn leads fetch failed: {json}");

            var results = new List<Dictionary<string, JsonElement>>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("elements", out var elements))
            {
                foreach (var el in elements.EnumerateArray())
                {
                    var row = new Dictionary<string, JsonElement>();
                    foreach (var prop in el.EnumerateObject())
                        row[prop.Name] = prop.Value;
                    results.Add(row);
                }
            }
            return results;
        }

        // ── Posting ──────────────────────────────────────────────────

        public async Task<string> PostAsync(
            string accessToken,
            string linkedInUserId,
            string text,
            string? articleUrl = null,
            string? imageBase64 = null,
            string? imageMime = null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

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
                var asset = await _RegisterImageUploadAsync(accessToken, linkedInUserId);
                await _UploadImageAsync(asset.UploadUrl, imageBase64, imageMime);

                shareContent = new
                {
                    shareCommentary = new { text },
                    shareMediaCategory = "IMAGE",
                    media = new[] { new { status = "READY", media = asset.Asset } }
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
            content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
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