using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using NuGet.Protocol;
using SocialMediaPanel.Data;
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

        public TwitterController(
            AppDbContext context,
            IConfiguration config,
            IHttpClientFactory httpClientFactory,
            ILogger<TwitterController> logger)
        {
            _context = context;
            _config = config;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        // ══════════════════════════════════════════════════════
        // GET /Twitter/Index
        // Twitter dashboard
        // ══════════════════════════════════════════════════════
        public async Task<IActionResult> Index()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return RedirectToAction("Login", "Account");

            var user = await _context.Users
                .Where(u => u.Id == userId)
                .FirstOrDefaultAsync();

            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Login", "Account");
            }

            ViewBag.UserName = user.Name;
            ViewBag.UserEmail = user.Email;
            ViewBag.CompanyName = user.CompanyName ?? "My Workspace";

            // Twitter account check karo
            var twitterAccount = await _context.TwitterAccounts
                .Where(t => t.UserId == userId && t.IsActive)
                .FirstOrDefaultAsync();

            ViewBag.TwitterConnected = twitterAccount != null;
            ViewBag.TwitterAccount = twitterAccount;

            // Posted tweets history
            var tweets = await _context.TweetsPosted
                .Where(t => t.UserId == userId)
                .OrderByDescending(t => t.CreatedAt)
                .Take(20)
                .ToListAsync();

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
            try
            {
                var userId = HttpContext.Session.GetInt32("UserId");
                if (userId == null)
                    return Unauthorized(new { success = false, message = "User not logged in" });

                var twitterAccount = await _context.TwitterAccounts
                    .FirstOrDefaultAsync(t => t.UserId == userId && t.IsActive);

                if (twitterAccount == null)
                    return BadRequest(new { success = false, message = "Twitter not connected" });

                var accessToken = twitterAccount.AccessToken;
                var twitterUserId = twitterAccount.TwitterUserId;

                var client = _httpClientFactory.CreateClient();

                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                client.DefaultRequestHeaders.Add("User-Agent", "MyApp");

                string url = $"https://api.twitter.com/2/users/{twitterUserId}/tweets" +
                             "?max_results=5&tweet.fields=created_at";

                var response = await client.GetAsync(url);
                var result = await response.Content.ReadAsStringAsync();

                // 🔥 Debug log
                Console.WriteLine("Status: " + response.StatusCode);
                Console.WriteLine("Response: " + result);

                if (!response.IsSuccessStatusCode)
                {
                    return BadRequest(new
                    {
                        success = false,
                        status = response.StatusCode,
                        error = result
                    });
                }

                return Content(result, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }
        // ══════════════════════════════════════════════════════
        // POST /Twitter/PostTweet
        // Tweet post karo
        // ══════════════════════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PostTweet(string tweetText)
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
                return Json(new { success = false, message = "Twitter connect nahi hai" });

            // Token refresh karo agar expire ho
            var validToken = await GetValidToken(account);
            if (string.IsNullOrEmpty(validToken))
                return Json(new { success = false, message = "Twitter token expire — reconnect karo" });

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", validToken);

            var payload = JsonSerializer.Serialize(new { text = tweetText });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("https://api.twitter.com/2/tweets", content);
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
                    message = "Tweet post ho gaya! ✅",
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
                    return Json(new { success = false, message = "Rate limit hit — 15 minute baad try karo" });

                return Json(new { success = false, message = "Tweet failed — dobara try karo" });
            }
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

            TempData["TwitterSuccess"] = "Twitter disconnect ho gaya.";
            return RedirectToAction("Index");
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
}