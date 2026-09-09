using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet.Protocol;
using SocialMediaPanel.Data;
using SocialMediaPanel.Hubs;
using SocialMediaPanel.Models;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SocialMediaPanel.Controllers
{
    public class TwitterController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TwitterController> _logger;
        private readonly IHubContext<InboxHub> _hub;

        public TwitterController(
            AppDbContext context,
            IConfiguration config,
            IHttpClientFactory httpClientFactory,
            ILogger<TwitterController> logger,
            IHubContext<InboxHub> hub)
        {
            _context = context;
            _config = config;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _hub = hub;
        }

        // ══════════════════════════════════════════════════════
        // GET /Twitter/Index
        // Twitter dashboard
        // ══════════════════════════════════════════════════════
        //public async Task<IActionResult> Index()
        //{
        //    var userId = HttpContext.Session.GetInt32("UserId");
        //    if (userId == null)
        //        return RedirectToAction("Login", "Account");

        //    var user = await _context.Users
        //        .Where(u => u.Id == userId)
        //        .FirstOrDefaultAsync();

        //    if (user == null)
        //    {
        //        HttpContext.Session.Clear();
        //        return RedirectToAction("Login", "Account");
        //    }

        //    ViewBag.UserName = user.Name;
        //    ViewBag.UserEmail = user.Email;
        //    ViewBag.CompanyName = user.CompanyName ?? "My Workspace";

        //    // Twitter account check karo
        //    var twitterAccount = await _context.TwitterAccounts
        //        .Where(t => t.UserId == userId && t.IsActive)
        //        .FirstOrDefaultAsync();

        //    ViewBag.TwitterConnected = twitterAccount != null;
        //    ViewBag.TwitterAccount = twitterAccount;

        //    // Posted tweets history
        //    var tweets = await _context.TweetsPosted
        //        .Where(t => t.UserId == userId)
        //        .OrderByDescending(t => t.CreatedAt)
        //        .Take(20)
        //        .ToListAsync();

        //    ViewBag.Tweets = tweets;

        //    return View();
        //}


        public async Task<IActionResult> Index()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return RedirectToAction("Login", "Account");

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Login", "Account");
            }

            ViewBag.UserName = user.Name;
            ViewBag.UserEmail = user.Email;
            ViewBag.CompanyName = user.CompanyName ?? "My Workspace";

            var twitterAccount = await _context.TwitterAccounts
                .FirstOrDefaultAsync(t => t.UserId == userId && t.IsActive);

            ViewBag.TwitterConnected = twitterAccount != null;
            ViewBag.TwitterAccount = twitterAccount;

            var tweets = new List<TweetPosted>();

            if (twitterAccount != null)
            {
                try
                {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", twitterAccount.AccessToken);

                    var url = $"https://api.twitter.com/2/users/{twitterAccount.TwitterUserId}/tweets" +
          "?max_results=10" +
          "&tweet.fields=created_at,attachments" +
          "&expansions=attachments.media_keys" +
          "&media.fields=url,preview_image_url,type";

                    var response = await client.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);

                        // Build media lookup dictionary
                        var mediaLookup = new Dictionary<string, string>();
                        if (doc.RootElement.TryGetProperty("includes", out var includes) &&
                            includes.TryGetProperty("media", out var mediaArray))
                        {
                            foreach (var m in mediaArray.EnumerateArray())
                            {
                                var key = m.TryGetProperty("media_key", out var mk) ? mk.GetString() : null;
                                var mUrl = m.TryGetProperty("url", out var mu) ? mu.GetString() : null;
                                if (key != null && mUrl != null)
                                    mediaLookup[key] = mUrl;
                            }
                        }

                        if (doc.RootElement.TryGetProperty("data", out var data))
                        {
                            foreach (var tweet in data.EnumerateArray())
                            {
                                var id = tweet.TryGetProperty("id", out var tid) ? tid.GetString() : null;
                                var text = tweet.TryGetProperty("text", out var ttxt) ? ttxt.GetString() : "";

                                // Strip t.co link at end
                                text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*https://t\.co/\S+$", "").Trim();

                                DateTime createdAt = DateTime.UtcNow;
                                if (tweet.TryGetProperty("created_at", out var tca))
                                    DateTime.TryParse(tca.GetString(), out createdAt);

                                // Find first media image URL
                                string? thumbUrl = null;
                                if (tweet.TryGetProperty("attachments", out var att) &&
                                    att.TryGetProperty("media_keys", out var keys))
                                {
                                    foreach (var k in keys.EnumerateArray())
                                    {
                                        var keyStr = k.GetString();
                                        if (keyStr != null && mediaLookup.TryGetValue(keyStr, out var imgUrl))
                                        {
                                            thumbUrl = imgUrl;
                                            break;
                                        }
                                    }
                                }

                                tweets.Add(new TweetPosted
                                {
                                    TweetId = id,
                                    TweetText = text,
                                    Status = "posted",
                                    CreatedAt = createdAt,
                                    UserId = userId.Value,
                                    TwitterAccountId = twitterAccount.TwitterUserId,
                                    MediaUrl = thumbUrl   // add this field — see Step 2
                                });
                            }
                        }
                    }
                    else
                    {
                        ViewBag.Error = "Failed to fetch tweets from Twitter API";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("Error fetching tweets from API: {Msg}", ex.Message);
                    ViewBag.Error = "Error fetching tweets";
                }
            }

            ViewBag.Tweets = tweets;

            return View();
        }
        // ══════════════════════════════════════════════════════
        // Step 1: Redirect user to Twitter login
        // ══════════════════════════════════════════════════════
        public IActionResult Connect()
        {
            var codeVerifier = GenerateCodeVerifier();
            var codeChallenge = GenerateCodeChallenge(codeVerifier);

            HttpContext.Session.SetString("tw_verifier", codeVerifier);

            string clientId = _config["TwitterAuth:ClientId"];
            string redirectUri = _config["TwitterAuth:RedirectUri"];
            string scope = _config["TwitterAuth:Scope"];

            var query = new Dictionary<string, string>
        {
            { "response_type", "code" },
            { "client_id", clientId },
            { "redirect_uri", redirectUri },
            { "scope", scope },
            { "state", Guid.NewGuid().ToString() },
            { "code_challenge", codeChallenge },
            { "code_challenge_method", "S256" }
        };

            string url = QueryHelpers.AddQueryString("https://twitter.com/i/oauth2/authorize", query);
            return Redirect(url);

        }

        // ══════════════════════════════════════════════════════
        // Step 2: Twitter redirects back here with ?code=...
        // ══════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Callback(string? code, string? state, string? error)
        {

            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            // 1. Check for Twitter Errors
            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            {
                TempData["TwitterError"] = "Twitter authentication failed.";
                return RedirectToAction("Index");
            }

            // 2. Retrieve PKCE Verifier from Session
            var codeVerifier = HttpContext.Session.GetString("tw_verifier");
            if (string.IsNullOrEmpty(codeVerifier))
            {
                TempData["TwitterError"] = "Session expired or PKCE verifier missing.";
                return RedirectToAction("Index");
            }

            var clientId = _config["TwitterAuth:ClientId"];
            var clientSecret = _config["TwitterAuth:ClientSecret"];
            var redirectUri = _config["TwitterAuth:RedirectUri"];

            var client = _httpClientFactory.CreateClient();

            // 3. Prepare the Token Request (Matching Python's oauth.fetch_token)
            var tokenRequestParameters = new Dictionary<string, string>
    {
        { "grant_type", "authorization_code" },
        { "code", code },
        { "redirect_uri", redirectUri! },
        { "code_verifier", codeVerifier },
        { "client_id", clientId! } // V2 often requires this in the body
    };

            var requestContent = new FormUrlEncodedContent(tokenRequestParameters);

            // 4. Set Basic Auth Header (Matching Python's HTTPBasicAuth)
            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);

            // 5. Exchange Code for Token
            var tokenResponse = await client.PostAsync("https://api.x.com/2/oauth2/token", requestContent);
            var tokenJson = await tokenResponse.Content.ReadAsStringAsync();


            if (!tokenResponse.IsSuccessStatusCode)
            {
                TempData["Error"] = "Token exchange failed: " + tokenJson;
                return RedirectToAction("Index");
            }

            var json = JObject.Parse(tokenJson);

            string accessToken = json["access_token"]?.ToString();
            string refreshToken = json["refresh_token"]?.ToString();
            int expiresIn = json["expires_in"] != null ? (int)json["expires_in"] : 7200;

            if (string.IsNullOrEmpty(accessToken))
            {
                TempData["Error"] = "Access token missing.";
                return RedirectToAction("Index");
            }

            // 🔹 STEP 2: FETCH USER INFO
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            var userRes = await client.GetAsync(
                "https://api.twitter.com/2/users/me?user.fields=name,username,profile_image_url");

            var userJson = await userRes.Content.ReadAsStringAsync();

            // Default values (important)
            string twitterUserId = "";
            string twitterName = "";
            string twitterHandle = "";
            string profileImg = "";

            // ✅ SAFE PARSE (no crash)
            try
            {
                using var userDoc = JsonDocument.Parse(userJson);

                if (userDoc.RootElement.TryGetProperty("data", out var data))
                {
                    twitterUserId = data.TryGetProperty("id", out var tid) ? tid.GetString() : "";
                    twitterName = data.TryGetProperty("name", out var tn) ? tn.GetString() : "";
                    twitterHandle = data.TryGetProperty("username", out var th) ? th.GetString() : "";
                    profileImg = data.TryGetProperty("profile_image_url", out var pi) ? pi.GetString() : "";
                }
                else
                {
                    // 🔥 API returned error instead of data
                    Console.WriteLine("Twitter user API error: " + userJson);
                }
            }
            catch (Exception ex)
            {
                // 🔥 JSON parse error
                Console.WriteLine("User parse error: " + ex.Message);
            }

            // 🔹 STEP 3: SAVE TO DB
            var existing = await _context.TwitterAccounts
                .FirstOrDefaultAsync(t => t.UserId == userId.Value);

            if (existing != null)
            {
                existing.TwitterUserId = twitterUserId ?? "";
                existing.TwitterUsername = twitterHandle ?? "";
                existing.TwitterName = twitterName ?? "";
                existing.ProfileImageUrl = profileImg ?? "";
                existing.AccessToken = accessToken;
                existing.RefreshToken = refreshToken;
                existing.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
                existing.IsActive = true;
            }
            else
            {
                _context.TwitterAccounts.Add(new TwitterAccount
                {
                    UserId = userId.Value,
                    TwitterUserId = twitterUserId,
                    TwitterUsername = twitterHandle ?? "",
                    TwitterName = twitterName ?? "",
                    ProfileImageUrl = profileImg,
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();

            TempData["Success"] = $"Twitter connected! @{twitterHandle}";
            return RedirectToAction("Index");
        }


        // Route renamed to match the view's actual call ('/Twitter/GetReplies') —
        // the old name (GetTweetReplies) never matched via default routing, so
        // this always 404'd. Response reshaped to {success, replies:[{username,
        // text, created_at}]} to match what the view's JS reads — it was
        // previously handed Twitter's raw {data:[...]} shape with no username
        // (needs the author_id -> user expansion) and no "replies" key at all.
        [HttpGet]
        [Route("Twitter/GetReplies")]
        public async Task<IActionResult> GetReplies(string tweetId)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return Unauthorized(new { success = false, message = "Not logged in" });

            var twitterAccount = await _context.TwitterAccounts
                    .FirstOrDefaultAsync(t => t.UserId == userId && t.IsActive);

            if (twitterAccount == null)
                return Json(new { success = false, message = "Twitter not connected" });

            var accessToken = twitterAccount.AccessToken;

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            // Note: tweets/search/recent only covers the last 7 days — a Twitter/X
            // API restriction, not something this app can work around without
            // elevated/full-archive access.
            var url =
                $"https://api.twitter.com/2/tweets/search/recent" +
                $"?query=conversation_id:{tweetId}" +
                $"&tweet.fields=created_at,author_id" +
                $"&expansions=author_id&user.fields=username";

            var response = await client.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return Json(new { success = false, message = json });

            var replies = new List<object>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var usernames = new Dictionary<string, string>();
                if (root.TryGetProperty("includes", out var includes) &&
                    includes.TryGetProperty("users", out var users))
                {
                    foreach (var u in users.EnumerateArray())
                    {
                        var uid = u.TryGetProperty("id", out var uidProp) ? uidProp.GetString() : null;
                        var uname = u.TryGetProperty("username", out var unameProp) ? unameProp.GetString() : null;
                        if (uid != null && uname != null) usernames[uid] = uname;
                    }
                }

                if (root.TryGetProperty("data", out var data))
                {
                    foreach (var tweet in data.EnumerateArray())
                    {
                        var authorId = tweet.TryGetProperty("author_id", out var aid) ? aid.GetString() : null;
                        replies.Add(new
                        {
                            username = authorId != null && usernames.TryGetValue(authorId, out var un) ? un : "user",
                            text = tweet.TryGetProperty("text", out var t) ? t.GetString() : "",
                            created_at = tweet.TryGetProperty("created_at", out var ca) ? ca.GetString() : ""
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("GetReplies parse error: {Msg}", ex.Message);
            }

            return Json(new { success = true, replies });
        }

        [HttpPost]
        public async Task<IActionResult> CreateTweet(string postContent)
        {
            try
            {
                var userId = HttpContext.Session.GetInt32("UserId");
                if (userId == null)
                    return Unauthorized(new { success = false, message = "User not logged in" });

                // 1. Get the stored Twitter account details
                var twitterAccount = await _context.TwitterAccounts
                    .FirstOrDefaultAsync(t => t.UserId == userId && t.IsActive);

                if (twitterAccount == null)
                    return BadRequest(new { success = false, message = "Twitter not connected" });

                // 2. Token Refresh Logic (Crucial for X API)
                // If your token is expired, you would call your refresh method here
                // similar to your Python self.xauth.get_access_token() logic.

                var accessToken = twitterAccount.AccessToken;

                // 3. Prepare the HTTP Client
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                // 4. Prepare the JSON Payload (Matching json={"text": post})
                var tweetData = new { text = postContent };

                // 5. Send POST request to https://api.x.com/2/tweets
                var response = await client.PostAsJsonAsync("https://api.x.com/2/tweets", tweetData);
                var result = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    // If this fails with 401/403, check if it's the "CreditsDepleted" issue
                    return BadRequest(new { success = false, message = "Failed to post tweet", detail = result });
                }

                return Ok(new { success = true, message = "Tweet posted successfully!", data = result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }


        [HttpGet]
        public async Task<IActionResult> GetMyTweets()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return Unauthorized(new { success = false, message = "Not logged in" });

            var twitterAccount = await _context.TwitterAccounts
                .FirstOrDefaultAsync(t => t.UserId == userId && t.IsActive);

            if (twitterAccount == null)
                return Json(new { success = false, message = "Twitter not connected" });

            var client = _httpClientFactory.CreateClient();

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", twitterAccount.AccessToken);

            // UPDATED URL: Added expansions and media.fields
            // attachments.media_keys -> links tweets to media
            // media.fields=url -> gives the actual image link
            string url = $"https://api.twitter.com/2/users/{twitterAccount.TwitterUserId}/tweets" +
                         "?max_results=5" +
                         "&tweet.fields=created_at,attachments" +
                         "&expansions=attachments.media_keys" +
                         "&media.fields=url,preview_image_url,type";

            var response = await client.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return Json(new { success = false, message = json });
            }

            return Content(json, "application/json");
        }
        // ══════════════════════════════════════════════════════
        // POST /Twitter/PostTweet
        // Tweet post karo
        // ══════════════════════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PostTweet(string tweetText, IFormFile? media)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return Json(new { success = false, message = "Login required" });

            if (string.IsNullOrWhiteSpace(tweetText))
                return Json(new { success = false, message = "Tweet text empty hai" });

            if (tweetText.Length > 280)
                return Json(new { success = false, message = "Tweet 280 characters se zyada nahi ho sakta" });


            var account = await _context.TwitterAccounts
                .Where(t => t.UserId == userId && t.IsActive)
                .FirstOrDefaultAsync();

            if (account == null)
                return Json(new { success = false, message = "Twitter not connected" });

            // Token refresh karo agar expire ho
            var validToken = await GetValidToken(account);
            if (string.IsNullOrEmpty(validToken))
                return Json(new { success = false, message = "Twitter token expire — reconnect karo" });


            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", validToken);


            string? mediaId = null;

            if (media != null)
            {
                mediaId = await UploadMedia(media, validToken);

                if (string.IsNullOrEmpty(mediaId))
                {
                    return Json(new
                    {
                        success = false,
                        message = "Media upload failed"
                    });
                }
            }

            object tweetPayload;

            if (!string.IsNullOrEmpty(mediaId))
            {
                tweetPayload = new
                {
                    text = tweetText,
                    media = new
                    {
                        media_ids = new[] { mediaId }
                    }
                };
            }
            else
            {
                tweetPayload = new
                {
                    text = tweetText
                };
            }

            var jsonPayload = System.Text.Json.JsonSerializer.Serialize(tweetPayload);

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", validToken);

            var content = new StringContent(jsonPayload, Encoding.UTF8);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            var response = await client.PostAsync(
                "https://api.twitter.com/2/tweets",
                content
            );

            var respJson = await response.Content.ReadAsStringAsync();

            _logger.LogInformation("Tweet response: {Json}", respJson);

            // Tweet history save karo
            var tweetRecord = new TweetPosted
            {
                UserId = userId.Value,
                TwitterAccountId = account.TwitterUserId,
                TweetText = tweetText,
                CreatedAt = DateTime.UtcNow
            };

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(respJson);
                var data = doc.RootElement.TryGetProperty("data", out var d) ? d : (JsonElement?)null;
                var tweetId = data?.TryGetProperty("id", out var tid) == true ? tid.GetString() : null;

                tweetRecord.TweetId = tweetId;
                tweetRecord.Status = "posted";
                tweetRecord.PostedAt = DateTime.UtcNow;

                _context.TweetsPosted.Add(tweetRecord);
                await _context.SaveChangesAsync();

                return Json(new
                {
                    success = true,
                    message = "Tweet posted ✅",
                    tweetId = tweetId
                });
            }
            else
            {
                tweetRecord.Status = "failed";
                tweetRecord.ErrorMessage = respJson;

                _context.TweetsPosted.Add(tweetRecord);
                await _context.SaveChangesAsync();

                // Rate limit check
                if ((int)response.StatusCode == 429)
                    return Json(new { success = false, message = "Rate limit hit — retry in 15 minute" });

                return Json(new { success = false, message = "Tweet failed — retry" });
            }
        }


        private async Task<string> UploadMedia(IFormFile file, string token)
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // This boundary is important for multipart consistency
            var boundary = Guid.NewGuid().ToString();
            using var form = new MultipartFormDataContent(boundary);

            var stream = file.OpenReadStream();
            var fileContent = new StreamContent(stream);

            // 1. MUST set the correct Content-Type for the image part
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);

            // 2. The field name must be "media" for the v2/media/upload endpoint
            // We add it with the filename to ensure the header is complete
            form.Add(fileContent, "media", file.FileName);

            // 3. MANDATORY: You must specify the category for v2 to process it
            form.Add(new StringContent("tweet_image"), "media_category");

            // 4. Hit the V2 endpoint
            var response = await client.PostAsync("https://api.twitter.com/2/media/upload", form);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // Check this log! If it's still 400, the JSON will tell us 
                // if it's "Invalid media type" or "Media category missing"
                _logger.LogError("Twitter API Detail: {Json}", json);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            // V2 returns: { "data": { "id": "..." } }
            return doc.RootElement.GetProperty("data").GetProperty("id").GetString();
        }

        // ══════════════════════════════════════════════════════
        // POST /Twitter/Disconnect
        // Twitter disconnect karo
        // ══════════════════════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Disconnect()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return RedirectToAction("Login", "Account");

            var account = await _context.TwitterAccounts
                .Where(t => t.UserId == userId && t.IsActive)
                .FirstOrDefaultAsync();

            if (account != null)
            {
                account.IsActive = false;
                account.AccessToken = null;
                account.RefreshToken = null;
                await _context.SaveChangesAsync();
            }

            TempData["TwitterSuccess"] = "Twitter disconnected.";
            return RedirectToAction("Index");
        }

        // ══════════════════════════════════════════════════════
        // GET /Twitter/SyncMentions — pull recent @mentions into the
        // Smart Inbox (page_comments, same table Facebook/Instagram
        // mentions already use — Platform is a free-text column, no
        // schema change needed). Manual trigger (called from the Inbox's
        // Reload button) rather than a background poll: X's mentions-
        // timeline endpoint has a tight per-15-minute rate limit even on
        // paid tiers, and there's no webhook equivalent to push these in
        // real time the way Meta does for Facebook/Instagram.
        // ══════════════════════════════════════════════════════
        [HttpPost]
        [Route("Twitter/SyncMentions")]
        public async Task<IActionResult> SyncMentions()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return Json(new { success = false, message = "Not logged in" });

            var account = await _context.TwitterAccounts
                .FirstOrDefaultAsync(t => t.UserId == userId && t.IsActive);
            if (account == null || string.IsNullOrEmpty(account.TwitterUserId))
                return Json(new { success = false, message = "Twitter not connected" });

            var validToken = await GetValidToken(account);
            if (string.IsNullOrEmpty(validToken))
                return Json(new { success = false, message = "Twitter token expired — reconnect required" });

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", validToken);

                var url = $"https://api.twitter.com/2/users/{account.TwitterUserId}/mentions" +
                          "?max_results=25" +
                          "&tweet.fields=created_at,author_id" +
                          "&expansions=author_id&user.fields=username,name";

                var resp = await client.GetAsync(url);
                var json = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Twitter mentions sync failed: {Status} {Body}", resp.StatusCode, json);
                    return Json(new { success = false, message = "Twitter API error", detail = json });
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var names = new Dictionary<string, string>();
                if (root.TryGetProperty("includes", out var includes) &&
                    includes.TryGetProperty("users", out var users))
                {
                    foreach (var u in users.EnumerateArray())
                    {
                        var uid = u.TryGetProperty("id", out var uidP) ? uidP.GetString() : null;
                        var uname = u.TryGetProperty("username", out var unameP) ? unameP.GetString() : null;
                        if (uid != null && uname != null) names[uid] = uname;
                    }
                }

                int added = 0;
                if (root.TryGetProperty("data", out var data))
                {
                    foreach (var tweet in data.EnumerateArray())
                    {
                        var tweetId = tweet.TryGetProperty("id", out var tid) ? tid.GetString() : null;
                        if (string.IsNullOrEmpty(tweetId)) continue;

                        // Dedup: skip if already synced from a previous call.
                        var exists = await _context.PageComments.AnyAsync(c => c.CommentId == tweetId && c.Platform == "twitter");
                        if (exists) continue;

                        var authorId = tweet.TryGetProperty("author_id", out var aid) ? aid.GetString() : null;
                        var text = tweet.TryGetProperty("text", out var t) ? t.GetString() : "";
                        DateTime? createdAt = null;
                        if (tweet.TryGetProperty("created_at", out var ca) && DateTime.TryParse(ca.GetString(), out var parsed))
                            createdAt = parsed.ToUniversalTime();

                        _context.PageComments.Add(new PageComment
                        {
                            CommentId = tweetId,
                            PageId = account.TwitterUserId,
                            PostId = "",
                            SenderId = authorId ?? "",
                            SenderName = authorId != null && names.TryGetValue(authorId, out var uname2) ? "@" + uname2 : "Unknown",
                            Message = text,
                            Platform = "twitter",
                            CommentType = "mention",
                            CommentTime = createdAt,
                            CreatedAt = DateTime.UtcNow
                        });
                        added++;
                    }
                }

                if (added > 0)
                {
                    await _context.SaveChangesAsync();
                    await _hub.Clients.All.SendAsync("inboxChanged");
                }

                return Json(new { success = true, added });
            }
            catch (Exception ex)
            {
                _logger.LogError("Twitter mentions sync error: {Msg}", ex.Message);
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ══════════════════════════════════════════════════════
        // GET /Twitter/TweetHistory
        // Posted tweets ki list
        // ══════════════════════════════════════════════════════
        public async Task<IActionResult> TweetHistory()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return RedirectToAction("Login", "Account");

            var tweets = await _context.TweetsPosted
                .Where(t => t.UserId == userId)
                .OrderByDescending(t => t.CreatedAt)
                .Take(50)
                .ToListAsync();

            return Json(new { success = true, data = tweets });
        }

        // ══════════════════════════════════════════════════════
        // PRIVATE — Valid token lo (refresh if expired)
        // ══════════════════════════════════════════════════════
        private async Task<string?> GetValidToken(TwitterAccount account)
        {
            // Token expire nahi hua — seedha return karo
            if (account.TokenExpiresAt.HasValue
                && account.TokenExpiresAt.Value > DateTime.UtcNow.AddMinutes(5))
            {
                return account.AccessToken;
            }

            // Token expire hua — refresh karo
            if (string.IsNullOrEmpty(account.RefreshToken))
            {
                _logger.LogWarning("Twitter refresh token nahi hai — reconnect needed");
                return null;
            }

            try
            {
                var clientId = _config["TwitterAuth:ClientId"];
                var clientSecret = _config["TwitterAuth:ClientSecret"];
                var client = _httpClientFactory.CreateClient();

                var credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", credentials);

                var refreshRequest = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "grant_type",    "refresh_token"        },
                    { "refresh_token", account.RefreshToken   },
                    { "client_id",     clientId!              }
                });

                var response = await client.PostAsync(
                    "https://api.twitter.com/2/oauth2/token", refreshRequest);
                var respJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Twitter token refresh failed: {Json}", respJson);
                    return null;
                }

                using var doc = JsonDocument.Parse(respJson);
                var root = doc.RootElement;
                var newAccess = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
                var newRefresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
                var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 7200;

                if (string.IsNullOrEmpty(newAccess)) return null;

                // DB update karo
                account.AccessToken = newAccess;
                account.RefreshToken = newRefresh ?? account.RefreshToken;
                account.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Twitter token refreshed ✅");
                return newAccess;
            }
            catch (Exception ex)
            {
                _logger.LogError("Twitter token refresh error: {Msg}", ex.Message);
                return null;
            }
        }

        // ── PKCE Helpers ──
        private static string GenerateCodeVerifier()
        {
            var bytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Base64UrlEncode(bytes);
        }

        private static string GenerateCodeChallenge(string verifier)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(verifier));
            return Base64UrlEncode(hash);
        }

        private static string Base64UrlEncode(byte[] input) =>
            Convert.ToBase64String(input)
                .Replace("+", "-")
                .Replace("/", "_")
                .Replace("=", "");
    }

    public class TwitterApiResponse
    {
        public List<TwitterTweetItem> Data { get; set; }
    }

    public class TwitterTweetItem
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public DateTime Created_At { get; set; }
    }
}