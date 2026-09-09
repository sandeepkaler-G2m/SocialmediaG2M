using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SocialMediaPanel.Services
{
    /// <summary>
    /// All Gmail / Google OAuth operations.
    /// Registered via: builder.Services.AddHttpClient&lt;GmailService&gt;()
    ///
    /// What you can do with Gmail integration:
    ///   1.  BuildOAuthUrl()           → redirect user to Google consent screen
    ///   2.  ExchangeCodeAsync()       → get access + refresh tokens
    ///   3.  GetProfileAsync()         → get email address + display name
    ///   4.  RefreshAccessTokenAsync() → renew expired access token
    ///   5.  GetEmailsAsync()          → list inbox emails (subject, sender, snippet)
    ///   6.  GetEmailBodyAsync()       → full email content (HTML or plain text)
    ///   7.  SendEmailAsync()          → send a new email
    ///   8.  ReplyToEmailAsync()       → reply to a specific thread
    ///   9.  GetLabelsAsync()          → list Gmail labels (Inbox, Sent, etc.)
    ///   10. CreateLabelAsync()        → create a custom label
    ///   11. ApplyLabelAsync()         → label a message (e.g. "Campaign")
    ///   12. GetUnreadCountAsync()     → count unread messages
    ///   13. SearchEmailsAsync()       → search by query (e.g. "from:noreply@meta.com")
    ///   14. RevokeTokenAsync()        → disconnect / revoke access
    /// </summary>
    public class GmailService
    {
        private readonly HttpClient _http;
        private readonly IConfiguration _config;

        // appsettings.json keys:
        // "Google": {
        //   "ClientId":     "xxxx.apps.googleusercontent.com",
        //   "ClientSecret": "GOCSPX-xxxx",
        //   "RedirectUri":  "https://localhost:7276/Integrations/Callback/gmail"
        // }
        private string ClientId => _config["Google:ClientId"] ?? throw new InvalidOperationException("Google:ClientId not set");
        private string ClientSecret => _config["Google:ClientSecret"] ?? throw new InvalidOperationException("Google:ClientSecret not set");
        private string RedirectUri => _config["Google:RedirectUri"] ?? throw new InvalidOperationException("Google:RedirectUri not set");

        private static readonly string[] Scopes =
        {
            "https://www.googleapis.com/auth/gmail.readonly",       // read emails
            "https://www.googleapis.com/auth/gmail.send",           // send emails
            "https://www.googleapis.com/auth/gmail.modify",         // label / archive
            "https://www.googleapis.com/auth/userinfo.email",       // get email address
            "https://www.googleapis.com/auth/userinfo.profile",     // get display name
        };

        public GmailService(HttpClient http, IConfiguration config)
        {
            _http = http;
            _config = config;
        }

        // ══════════════════════════════════════════════════════════════
        // 1. BUILD OAUTH URL
        // ══════════════════════════════════════════════════════════════
        public string BuildOAuthUrl(string state)
        {
            return "https://accounts.google.com/o/oauth2/v2/auth"
                 + $"?client_id={Uri.EscapeDataString(ClientId)}"
                 + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
                 + $"&response_type=code"
                 + $"&scope={Uri.EscapeDataString(string.Join(" ", Scopes))}"
                 + $"&state={Uri.EscapeDataString(state)}"
                 + "&access_type=offline"      // get refresh token
                 + "&prompt=consent";           // always show consent to get refresh token
        }

        // ══════════════════════════════════════════════════════════════
        // 2. EXCHANGE CODE FOR TOKENS
        // ══════════════════════════════════════════════════════════════
        public async Task<GmailTokenResult> ExchangeCodeAsync(string code)
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["redirect_uri"] = RedirectUri,
                ["grant_type"] = "authorization_code"
            });

            var r = await _http.PostAsync("https://oauth2.googleapis.com/token", body);
            var json = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode)
                throw new Exception($"Gmail code exchange failed ({(int)r.StatusCode}): {json}");

            var payload = JsonSerializer.Deserialize<GoogleTokenPayload>(json)
                          ?? throw new Exception("Empty token response from Google");

            return new GmailTokenResult
            {
                AccessToken = payload.AccessToken ?? throw new Exception("No access token"),
                RefreshToken = payload.RefreshToken ?? "",
                ExpiresIn = payload.ExpiresIn,
                Scope = payload.Scope ?? ""
            };
        }

        // ══════════════════════════════════════════════════════════════
        // 3. GET GOOGLE PROFILE (email + name)
        // ══════════════════════════════════════════════════════════════
        public async Task<GmailProfile> GetProfileAsync(string accessToken)
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                "https://www.googleapis.com/oauth2/v2/userinfo");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var r = await _http.SendAsync(req);
            r.EnsureSuccessStatusCode();
            var json = JsonSerializer.Deserialize<GoogleUserInfo>(
                           await r.Content.ReadAsStringAsync())
                       ?? throw new Exception("Empty profile response");

            return new GmailProfile
            {
                GoogleId = json.Id ?? "",
                Email = json.Email ?? "",
                DisplayName = json.Name ?? "",
                PictureUrl = json.Picture ?? ""
            };
        }

        // ══════════════════════════════════════════════════════════════
        // 4. REFRESH ACCESS TOKEN
        // ══════════════════════════════════════════════════════════════
        public async Task<string> RefreshAccessTokenAsync(string refreshToken)
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token"
            });

            var r = await _http.PostAsync("https://oauth2.googleapis.com/token", body);
            var json = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode)
                throw new Exception($"Gmail token refresh failed ({(int)r.StatusCode}): {json}");

            var payload = JsonSerializer.Deserialize<GoogleTokenPayload>(json);
            return payload?.AccessToken ?? throw new Exception("Token refresh failed");
        }

        // ══════════════════════════════════════════════════════════════
        // 5. GET EMAILS (inbox list) using Google.Apis client
        // ══════════════════════════════════════════════════════════════
        public async Task<List<GmailMessage>> GetEmailsAsync(
            string accessToken,
            int maxResults = 20,
            string query = "")
        {
            try
            {
                // Build Google.Apis GmailService with the access token
                var credential = GoogleCredential.FromAccessToken(accessToken);
                var gmailSvc = new Google.Apis.Gmail.v1.GmailService(
                    new Google.Apis.Services.BaseClientService.Initializer
                    {
                        HttpClientInitializer = credential,
                        ApplicationName = "SocialMediaPanel"
                    });

                // List message IDs
                var listReq = gmailSvc.Users.Messages.List("me");
                listReq.MaxResults = maxResults;
                listReq.Q = string.IsNullOrEmpty(query) ? "in:inbox" : query;

                var listResp = await listReq.ExecuteAsync();

                if (listResp?.Messages == null || listResp.Messages.Count == 0)
                    return new List<GmailMessage>();

                // Fetch metadata for each message in parallel
                var tasks = listResp.Messages.Select(async m =>
                {
                    var msgReq = gmailSvc.Users.Messages.Get("me", m.Id);
                    msgReq.Format = Google.Apis.Gmail.v1.UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
                    msgReq.MetadataHeaders = new[] { "Subject", "From", "To", "Date" };
                    return await msgReq.ExecuteAsync();
                });

                var details = await Task.WhenAll(tasks);

                return details
                    .Where(d => d != null)
                    .Select(d =>
                    {
                        string GetHdr(string name) =>
                            d.Payload?.Headers?
                             .FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
                             ?.Value ?? "";

                        return new GmailMessage
                        {
                            MessageId = d.Id ?? "",
                            ThreadId = d.ThreadId ?? "",
                            Subject = GetHdr("Subject"),
                            From = GetHdr("From"),
                            To = GetHdr("To"),
                            Date = GetHdr("Date"),
                            Snippet = d.Snippet ?? "",
                            IsUnread = d.LabelIds?.Contains("UNREAD") ?? false
                        };
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                throw new Exception("GetEmailsAsync failed: " + ex.Message, ex);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // PUBLIC: Extract body from a Google.Apis Message object
        // Called directly by GmailController.Read to avoid a second API call
        // ══════════════════════════════════════════════════════════════
        public string ExtractBodyFromMessage(Google.Apis.Gmail.v1.Data.Message msg)
        {
            if (msg?.Payload == null) return "";
            return _ExtractBodyFromApiPart(msg.Payload);
        }

        // ══════════════════════════════════════════════════════════════
        // 6. GET FULL EMAIL BODY using Google.Apis client
        // ══════════════════════════════════════════════════════════════
        public async Task<string> GetEmailBodyAsync(string accessToken, string messageId)
        {
            try
            {
                var credential = GoogleCredential.FromAccessToken(accessToken);
                var gmailSvc = new Google.Apis.Gmail.v1.GmailService(
                    new Google.Apis.Services.BaseClientService.Initializer
                    {
                        HttpClientInitializer = credential,
                        ApplicationName = "SocialMediaPanel"
                    });

                var req = gmailSvc.Users.Messages.Get("me", messageId);
                req.Format = Google.Apis.Gmail.v1.UsersResource.MessagesResource.GetRequest.FormatEnum.Full;

                var msg = await req.ExecuteAsync();
                if (msg?.Payload == null) return "";

                return _ExtractBodyFromApiPart(msg.Payload);
            }
            catch (Exception ex)
            {
                throw new Exception("GetEmailBodyAsync failed: " + ex.Message, ex);
            }
        }

        // Extract HTML body from Google.Apis MessagePart
        private static string _ExtractBodyFromApiPart(Google.Apis.Gmail.v1.Data.MessagePart part)
        {
            if (part == null) return "";

            // Prefer text/html
            if (part.MimeType == "text/html" && !string.IsNullOrEmpty(part.Body?.Data))
                return DecodeBase64Url(part.Body.Data);

            // Walk into parts recursively
            if (part.Parts != null)
            {
                // First pass: look for text/html
                foreach (var p in part.Parts)
                {
                    if (p.MimeType == "text/html" && !string.IsNullOrEmpty(p.Body?.Data))
                        return DecodeBase64Url(p.Body.Data);
                }
                // Second pass: recurse
                foreach (var p in part.Parts)
                {
                    var body = _ExtractBodyFromApiPart(p);
                    if (!string.IsNullOrEmpty(body)) return body;
                }
            }

            // Fallback: plain text
            if (part.MimeType == "text/plain" && !string.IsNullOrEmpty(part.Body?.Data))
            {
                var text = DecodeBase64Url(part.Body.Data);
                return "<pre style='white-space:pre-wrap;font-family:inherit'>" + System.Net.WebUtility.HtmlEncode(text) + "</pre>";
            }

            return "";
        }

        private static string DecodeBase64Url(string data)
        {
            var base64 = data.Replace('-', '+').Replace('_', '/');
            // Pad to multiple of 4
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }

        // ══════════════════════════════════════════════════════════════
        // 7. SEND EMAIL — using Google.Apis
        // ══════════════════════════════════════════════════════════════
        public async Task<string> SendEmailAsync(
            string accessToken,
            string to,
            string subject,
            string bodyHtml,
            string? replyToMessageId = null)
        {
            try
            {
                var gmailSvc = _BuildGmailSvc(accessToken);

                var mime = _BuildMime(to, subject, bodyHtml, replyToMessageId);
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(mime))
                                     .Replace('+', '-').Replace('/', '_').TrimEnd('=');

                var msg = new Google.Apis.Gmail.v1.Data.Message { Raw = encoded };
                var req = gmailSvc.Users.Messages.Send(msg, "me");
                var res = await req.ExecuteAsync();
                return res?.Id ?? "";
            }
            catch (Exception ex)
            {
                throw new Exception("SendEmailAsync failed: " + ex.Message, ex);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 8. REPLY TO EMAIL THREAD — using Google.Apis
        // ══════════════════════════════════════════════════════════════
        public async Task<string> ReplyToEmailAsync(
            string accessToken,
            string threadId,
            string messageId,
            string to,
            string subject,
            string bodyHtml)
        {
            try
            {
                var gmailSvc = _BuildGmailSvc(accessToken);

                var mime = _BuildMime(to, "Re: " + subject, bodyHtml, messageId);
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(mime))
                                     .Replace('+', '-').Replace('/', '_').TrimEnd('=');

                var msg = new Google.Apis.Gmail.v1.Data.Message
                {
                    Raw = encoded,
                    ThreadId = threadId
                };
                var req = gmailSvc.Users.Messages.Send(msg, "me");
                var res = await req.ExecuteAsync();
                return res?.Id ?? "";
            }
            catch (Exception ex)
            {
                throw new Exception("ReplyToEmailAsync failed: " + ex.Message, ex);
            }
        }

        // ── Shared helper: build Google.Apis GmailService ─────────────
        private static Google.Apis.Gmail.v1.GmailService _BuildGmailSvc(string accessToken)
        {
            var credential = GoogleCredential.FromAccessToken(accessToken);
            return new Google.Apis.Gmail.v1.GmailService(
                new Google.Apis.Services.BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "SocialMediaPanel"
                });
        }

        // ══════════════════════════════════════════════════════════════
        // 9. GET LABELS
        // ══════════════════════════════════════════════════════════════
        public async Task<List<GmailLabel>> GetLabelsAsync(string accessToken)
        {
            var resp = await _GetAsync<GmailLabelList>(
                accessToken,
                "https://gmail.googleapis.com/gmail/v1/users/me/labels");

            return resp?.Labels?.Select(l => new GmailLabel
            {
                LabelId = l.Id ?? "",
                Name = l.Name ?? "",
                Type = l.Type ?? ""
            }).ToList() ?? new List<GmailLabel>();
        }

        // ══════════════════════════════════════════════════════════════
        // 10. CREATE LABEL
        // ══════════════════════════════════════════════════════════════
        public async Task<string> CreateLabelAsync(string accessToken, string labelName)
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                "https://gmail.googleapis.com/gmail/v1/users/me/labels");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { name = labelName }),
                Encoding.UTF8, "application/json");

            var r = await _http.SendAsync(req);
            r.EnsureSuccessStatusCode();

            var resp = JsonSerializer.Deserialize<GmailLabelItem>(
                           await r.Content.ReadAsStringAsync());
            return resp?.Id ?? "";
        }

        // ══════════════════════════════════════════════════════════════
        // 11. APPLY LABEL TO MESSAGE
        // ══════════════════════════════════════════════════════════════
        public async Task ApplyLabelAsync(
            string accessToken,
            string messageId,
            string labelId)
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{messageId}/modify");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { addLabelIds = new[] { labelId } }),
                Encoding.UTF8, "application/json");

            var r = await _http.SendAsync(req);
            r.EnsureSuccessStatusCode();
        }

        // ══════════════════════════════════════════════════════════════
        // 12. GET UNREAD COUNT
        // ══════════════════════════════════════════════════════════════
        public async Task<int> GetUnreadCountAsync(string accessToken)
        {
            var resp = await _GetAsync<GmailProfile2>(
                accessToken,
                "https://gmail.googleapis.com/gmail/v1/users/me/profile");
            return resp?.MessagesTotal ?? 0;
        }

        // ══════════════════════════════════════════════════════════════
        // 13. SEARCH EMAILS
        // ══════════════════════════════════════════════════════════════
        public async Task<List<GmailMessage>> SearchEmailsAsync(
            string accessToken,
            string query,
            int maxResults = 20)
        {
            return await GetEmailsAsync(accessToken, maxResults, query);
        }

        // ══════════════════════════════════════════════════════════════
        // 14. REVOKE TOKEN (disconnect)
        // ══════════════════════════════════════════════════════════════
        public async Task<bool> RevokeTokenAsync(string accessToken)
        {
            var r = await _http.PostAsync(
                $"https://oauth2.googleapis.com/revoke?token={Uri.EscapeDataString(accessToken)}",
                null);
            return r.IsSuccessStatusCode;
        }

        // ══ Private helpers ════════════════════════════════════════════
        private async Task<T?> _GetAsync<T>(string accessToken, string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var r = await _http.SendAsync(req);

            if (!r.IsSuccessStatusCode)
            {
                var errBody = await r.Content.ReadAsStringAsync();
                throw new Exception($"Gmail API {(int)r.StatusCode} at {url}: {errBody}");
            }

            var json = await r.Content.ReadAsStringAsync();

            // Debug: log response to check structure
            System.Diagnostics.Debug.WriteLine($"[GmailAPI] {url}\n{json[..Math.Min(json.Length, 500)]}");

            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true   // handles both camelCase and PascalCase
            });
        }

        private static string _GetHeader(GmailMessageDetail d, string name)
        {
            return d.Payload?.Headers?
                .FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
                ?.Value ?? "";
        }

        private static string _ExtractBody(GmailPart part)
        {
            if (part.MimeType == "text/html" && !string.IsNullOrEmpty(part.Body?.Data))
                return Encoding.UTF8.GetString(
                    Convert.FromBase64String(
                        part.Body.Data.Replace('-', '+').Replace('_', '/')));

            if (part.Parts != null)
                foreach (var p in part.Parts)
                {
                    var body = _ExtractBody(p);
                    if (!string.IsNullOrEmpty(body)) return body;
                }

            return "";
        }

        private static string _BuildMime(
            string to, string subject, string bodyHtml, string? inReplyTo)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"To: {to}");
            sb.AppendLine($"Subject: {subject}");
            sb.AppendLine("MIME-Version: 1.0");
            sb.AppendLine("Content-Type: text/html; charset=utf-8");
            if (!string.IsNullOrEmpty(inReplyTo))
                sb.AppendLine($"In-Reply-To: {inReplyTo}");
            sb.AppendLine();
            sb.AppendLine(bodyHtml);
            return sb.ToString();
        }
    }

    // ══ Result / DTO types ══════════════════════════════════════════════

    public class GmailTokenResult
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public int ExpiresIn { get; set; }
        public string Scope { get; set; } = "";
    }

    public class GmailProfile
    {
        public string GoogleId { get; set; } = "";
        public string Email { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string PictureUrl { get; set; } = "";
    }

    public class GmailMessage
    {
        public string MessageId { get; set; } = "";
        public string ThreadId { get; set; } = "";
        public string Subject { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string Date { get; set; } = "";
        public string Snippet { get; set; } = "";
        public bool IsUnread { get; set; }
    }

    public class GmailLabel
    {
        public string LabelId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
    }

    // ── Internal JSON payload classes ──────────────────────────────────
    internal class GoogleTokenPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }

    internal class GoogleUserInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string? Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("email")]
        public string? Email { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("picture")]
        public string? Picture { get; set; }
    }

    internal class GmailMessageList
    {
        [System.Text.Json.Serialization.JsonPropertyName("messages")]
        public List<GmailMessageRef>? Messages { get; set; }
    }

    internal class GmailMessageRef
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    internal class GmailMessageDetail
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string? Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("threadId")]
        public string? ThreadId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("labelIds")]
        public List<string>? LabelIds { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("snippet")]
        public string? Snippet { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("payload")]
        public GmailPart? Payload { get; set; }
    }

    internal class GmailPart
    {
        [System.Text.Json.Serialization.JsonPropertyName("mimeType")]
        public string? MimeType { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("headers")]
        public List<GmailHeader>? Headers { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("body")]
        public GmailBody? Body { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("parts")]
        public List<GmailPart>? Parts { get; set; }
    }

    internal class GmailHeader
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public string? Value { get; set; }
    }

    internal class GmailBody
    {
        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public string? Data { get; set; }
    }

    internal class GmailLabelList
    {
        [System.Text.Json.Serialization.JsonPropertyName("labels")]
        public List<GmailLabelItem>? Labels { get; set; }
    }

    internal class GmailLabelItem
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string? Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string? Type { get; set; }
    }

    internal class GmailSendResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    internal class GmailProfile2
    {
        [System.Text.Json.Serialization.JsonPropertyName("messagesTotal")]
        public int MessagesTotal { get; set; }
    }
}