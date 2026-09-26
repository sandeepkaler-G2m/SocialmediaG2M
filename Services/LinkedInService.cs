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

        // Scopes actually approved for this app (per the Developer Portal's
        // Products tab — confirmed 2026-09-22): Sign In with OpenID Connect
        // (openid/profile/email), Share on LinkedIn (w_member_social),
        // Lead Sync API (r_marketing_leadgen_automation, plus the
        // r_organization_admin/r_ads it bundles per LinkedIn's own "Getting
        // Access to Lead Sync" doc), Conversions API (rw_conversions, r_ads),
        // Events Management API (r_events, rw_events).
        //
        // Community Management API scopes (r_organization_social,
        // w_organization_social, rw_organization_admin) are NOT approved —
        // do not add them here until that product actually shows approved
        // in the Portal, for the same reason as before: an unrecognized/
        // unapproved scope can cause LinkedIn to reject the whole
        // authorization request.
        //
        // NOTE: adding scopes here forces every already-connected LinkedIn
        // user to reconnect — LinkedIn invalidates a token's authorization
        // when the requested scope set changes.
        private const string RestrictedScopesPendingApproval =
            "r_organization_social w_organization_social rw_organization_admin"; // Community Management API — not yet approved

        public string BuildOAuthUrl(string state)
        {
            var clientId = _config["LinkedIn:ClientId"];
            var redirectUri = _config["LinkedIn:RedirectUri"];

            var scopes = "openid profile email w_member_social "
                       + "r_marketing_leadgen_automation r_organization_admin r_ads "
                       + "rw_conversions r_events rw_events";

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

        // Every /rest/ call needs these two headers — LinkedIn-Version
        // pinned to a fixed month so behavior doesn't silently shift.
        private const string ApiVersion = "202601";
        private HttpRequestMessage _RestRequest(HttpMethod method, string url, string accessToken, object? body = null)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.Add("Linkedin-Version", ApiVersion);
            req.Headers.Add("X-Restli-Protocol-Version", "2.0.0");
            if (body != null)
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return req;
        }

        // ── Organization pages (needs r_organization_admin) ──
        public async Task<List<(string Urn, string Name, string? LogoUrl)>> GetOrganizationsAsync(string accessToken)
        {
            var req = _RestRequest(HttpMethod.Get, "https://api.linkedin.com/rest/organizationAcls?q=roleAssignee&role=ADMINISTRATOR&state=APPROVED", accessToken);
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn organizations fetch failed ({(int)resp.StatusCode}): {json}");

            var results = new List<(string, string, string?)>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("elements", out var elements))
            {
                foreach (var el in elements.EnumerateArray())
                {
                    var orgUrn = el.TryGetProperty("organization", out var t) ? t.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(orgUrn)) continue;

                    string name = orgUrn;
                    try
                    {
                        var orgId = orgUrn.Split(':').Last();
                        var nameReq = _RestRequest(HttpMethod.Get, $"https://api.linkedin.com/rest/organizations/{orgId}", accessToken);
                        var nameResp = await _http.SendAsync(nameReq);
                        if (nameResp.IsSuccessStatusCode)
                        {
                            using var nameDoc = JsonDocument.Parse(await nameResp.Content.ReadAsStringAsync());
                            if (nameDoc.RootElement.TryGetProperty("localizedName", out var ln))
                                name = ln.GetString() ?? orgUrn;
                        }
                    }
                    catch { /* fall back to URN as the display name */ }

                    results.Add((orgUrn, name, null));
                }
            }
            return results;
        }

        // ── Lead Gen Forms leads (needs r_marketing_leadgen_automation) ──
        // Uses the current /rest/leadFormResponses endpoint (the old
        // /v2/leadFormResponses?q=owner shape this previously called is
        // retired) — owner must be parenthesized per Restli 2.0 query syntax.
        public async Task<List<Dictionary<string, JsonElement>>> GetLeadsAsync(string accessToken, string organizationUrn, string leadType = "SPONSORED")
        {
            var url = "https://api.linkedin.com/rest/leadFormResponses"
                     + $"?q=owner&owner=(organization:{Uri.EscapeDataString(organizationUrn)})"
                     + $"&leadType=(leadType:{leadType})";

            var req = _RestRequest(HttpMethod.Get, url, accessToken);
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn leads fetch failed ({(int)resp.StatusCode}): {json}");

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

        // ── Fetch one lead's full form response by id (called after a
        // leadNotifications webhook delivery — see LinkedInWebhookController) ──
        public async Task<string> GetLeadFormResponseAsync(string accessToken, string leadResponseId)
        {
            var url = $"https://api.linkedin.com/rest/leadFormResponses/{Uri.EscapeDataString(leadResponseId)}";
            var req = _RestRequest(HttpMethod.Get, url, accessToken);
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn lead form response fetch failed ({(int)resp.StatusCode}): {json}");

            return json; // raw — caller stores it; LinkedIn's answers are questionId-keyed, not named fields
        }

        // ── Register a webhook to receive real-time lead notifications ──
        // ownerUrn: "urn:li:organization:{id}" or "urn:li:sponsoredAccount:{id}".
        // Returns the subscription id LinkedIn assigns (needed to delete it later).
        public async Task<string> RegisterLeadNotificationAsync(string accessToken, string ownerUrn, string webhookUrl, string leadType = "SPONSORED")
        {
            var ownerKey = ownerUrn.Contains(":organization:") ? "organization" : "sponsoredAccount";
            var body = new Dictionary<string, object>
            {
                ["webhook"] = webhookUrl,
                ["owner"] = new Dictionary<string, string> { [ownerKey] = ownerUrn },
                ["leadType"] = leadType
            };

            var req = _RestRequest(HttpMethod.Post, "https://api.linkedin.com/rest/leadNotifications", accessToken, body);
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn lead notification registration failed ({(int)resp.StatusCode}): {json}");

            if (resp.Headers.TryGetValues("x-restli-id", out var vals))
                return vals.FirstOrDefault() ?? "";
            return "";
        }

        // ── Events Management API (r_events / rw_events) ──────────────
        // Creates an event, then posts it (an event isn't visible/fetchable
        // until posted — see LinkedIn's Events docs). Returns (eventId,
        // liveVideoUrn, vanityName).
        public async Task<(string EventId, string? LiveVideoUrn, string VanityName)> CreateEventAsync(
            string accessToken, string organizerUrn, string name, string? description,
            string eventType, long startsAt, long? endsAt, string? externalUrl,
            string? privacyPolicyUrlForLeadGen = null)
        {
            object type = eventType switch
            {
                "online_livevideo" => new { online = new { format = new { liveVideo = new { endsAt } } } },
                "online_external" => new { online = new { format = new { external = new { endsAt, url = externalUrl } } } },
                "inperson" => new { inPerson = new { endsAt, url = externalUrl, address = new { } } },
                _ => throw new ArgumentException("Unknown eventType: " + eventType)
            };

            var body = new Dictionary<string, object?>
            {
                ["name"] = new { localized = new Dictionary<string, string> { ["en_US"] = name } },
                ["type"] = type,
                ["organizer"] = organizerUrn,
                ["startsAt"] = startsAt
            };
            if (!string.IsNullOrEmpty(description))
                body["description"] = new { localized = new Dictionary<string, object> { ["en_US"] = new { rawText = description } } };
            if (!string.IsNullOrEmpty(privacyPolicyUrlForLeadGen))
                body["leadGenFormSpec"] = new { privacyPolicyUrl = privacyPolicyUrlForLeadGen };

            var req = _RestRequest(HttpMethod.Post, "https://api.linkedin.com/rest/events", accessToken, body);
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn event creation failed ({(int)resp.StatusCode}): {json}");

            using var doc = JsonDocument.Parse(json);
            var eventId = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetRawText().Trim('"') : "";
            string? liveVideoUrn = null;
            if (doc.RootElement.TryGetProperty("type", out var typeEl) &&
                typeEl.TryGetProperty("online", out var onlineEl) &&
                onlineEl.TryGetProperty("format", out var fmtEl) &&
                fmtEl.TryGetProperty("liveVideo", out var lvEl) &&
                lvEl.TryGetProperty("liveVideo", out var lvUrnEl))
                liveVideoUrn = lvUrnEl.GetString();
            var vanityName = doc.RootElement.TryGetProperty("vanityName", out var vn) ? vn.GetString() ?? "" : "";

            return (eventId, liveVideoUrn, vanityName);
        }

        // Makes a created event publicly visible. contentReferenceUrn is
        // urn:li:liveVideo:{id} for live-video events, urn:li:event:{id}
        // for external/in-person. Returns the created post's URN.
        public async Task<string> PostEventAsync(string accessToken, string organizerUrn, string contentReferenceUrn)
        {
            var body = new
            {
                author = organizerUrn,
                commentary = "",
                visibility = "PUBLIC",
                distribution = new { feedDistribution = "MAIN_FEED", targetEntities = Array.Empty<object>(), thirdPartyDistributionChannels = Array.Empty<object>() },
                content = new { reference = new { id = contentReferenceUrn } },
                lifecycleState = "PUBLISHED",
                isReshareDisabledByAuthor = false
            };

            var req = _RestRequest(HttpMethod.Post, "https://api.linkedin.com/rest/posts", accessToken, body);
            var resp = await _http.SendAsync(req);

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn event post failed ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");

            return resp.Headers.TryGetValues("x-restli-id", out var vals) ? vals.FirstOrDefault() ?? "" : "";
        }

        public async Task<string> GetEventsByOrganizerAsync(string accessToken, string organizerUrn, int count = 20)
        {
            var url = $"https://api.linkedin.com/rest/events?q=eventsByOrganizer&organizer={Uri.EscapeDataString(organizerUrn)}&start=0&count={count}";
            var req = _RestRequest(HttpMethod.Get, url, accessToken);
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn events fetch failed ({(int)resp.StatusCode}): {json}");
            return json;
        }

        // ── Conversions API (rw_conversions / r_ads) — requires the
        // connected account to hold a role on the given ad account ──────
        public async Task<string> CreateConversionRuleAsync(string accessToken, string name, string adAccountUrn, string conversionType)
        {
            var body = new
            {
                name,
                account = adAccountUrn,
                conversionMethod = "CONVERSIONS_API",
                postClickAttributionWindowSize = 90,
                viewThroughAttributionWindowSize = 30,
                attributionType = "LAST_TOUCH_BY_CAMPAIGN",
                type = conversionType
            };

            var req = _RestRequest(HttpMethod.Post, "https://api.linkedin.com/rest/conversions", accessToken, body);
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn conversion rule creation failed ({(int)resp.StatusCode}): {json}");

            if (resp.Headers.TryGetValues("x-restli-id", out var vals))
                return vals.FirstOrDefault() ?? "";
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "";
        }

        public async Task StreamConversionEventAsync(
            string accessToken, string conversionUrn, string eventId, long conversionHappenedAtMs,
            decimal? amount, string? currencyCode, string userIdentifierType, string userIdentifierValue)
        {
            var body = new Dictionary<string, object?>
            {
                ["conversion"] = conversionUrn,
                ["conversionHappenedAt"] = conversionHappenedAtMs,
                ["user"] = new
                {
                    userIds = new[] { new { idType = userIdentifierType, idValue = userIdentifierValue } }
                },
                ["eventId"] = eventId
            };
            if (amount.HasValue && !string.IsNullOrEmpty(currencyCode))
                body["conversionValue"] = new { currencyCode, amount = amount.Value.ToString("F2") };

            var req = _RestRequest(HttpMethod.Post, "https://api.linkedin.com/rest/conversionEvents", accessToken, body);
            var resp = await _http.SendAsync(req);

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LinkedIn conversion event stream failed ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
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