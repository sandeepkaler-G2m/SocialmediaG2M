using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

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
        // GET /Twitter/Connect
        // Twitter OAuth 2.0 PKCE flow start
        // ══════════════════════════════════════════════════════
        public IActionResult Connect()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return RedirectToAction("Login", "Account");

            var clientId = _config["TwitterAuth:ClientId"];
            var redirectUri = _config["TwitterAuth:RedirectUri"];
            var scope = "tweet.read tweet.write users.read offline.access";

            // PKCE generate karo
            var codeVerifier = GenerateCodeVerifier();
            var codeChallenge = GenerateCodeChallenge(codeVerifier);
            var state = Guid.NewGuid().ToString();

            // Session mein save karo
            HttpContext.Session.SetString("TwitterCodeVerifier", codeVerifier);
            HttpContext.Session.SetString("TwitterState", state);

            var query = new Dictionary<string, string?>
            {
                { "response_type",         "code"      },
                { "client_id",             clientId    },
                { "redirect_uri",          redirectUri },
                { "scope",                 scope       },
                { "state",                 state       },
                { "code_challenge",        codeChallenge },
                { "code_challenge_method", "S256"      }
            };

            var url = QueryHelpers.AddQueryString(
                "https://x.com/i/oauth2/authorize", query);

            return Redirect(url);
        }

        // ══════════════════════════════════════════════════════
        // GET /Twitter/Callback
        // Twitter OAuth callback
        // ══════════════════════════════════════════════════════
        public async Task<IActionResult> Callback(
            string? code, string? state, string? error)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return RedirectToAction("Login", "Account");

            if (!string.IsNullOrEmpty(error))
            {
                TempData["TwitterError"] = "Twitter error.";
                return RedirectToAction("Index");
            }

            if (string.IsNullOrEmpty(code))
            {
                TempData["TwitterError"] = "Twitter error.";
                return RedirectToAction("Index");
            }

            var savedState = HttpContext.Session.GetString("TwitterState");
            var codeVerifier = HttpContext.Session.GetString("TwitterCodeVerifier");

            if (string.IsNullOrEmpty(codeVerifier))
            {
                TempData["TwitterError"] = "Session expired.";
                return RedirectToAction("Index");
            }

            var clientId = _config["TwitterAuth:ClientId"];
            var clientSecret = _config["TwitterAuth:ClientSecret"];
            var redirectUri = _config["TwitterAuth:RedirectUri"];

            // Token exchange karo
            var client = _httpClientFactory.CreateClient();

            var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "client_id",     clientId!    },
                { "grant_type",    "authorization_code" },
                { "code",          code         },
                { "redirect_uri",  redirectUri! },
                { "code_verifier", codeVerifier }
            });

            // Basic auth header
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);

            var tokenResponse = await client.PostAsync(
                "https://api.twitter.com/2/oauth2/token", tokenRequest);

            var tokenJson = await tokenResponse.Content.ReadAsStringAsync();

            _logger.LogInformation("Twitter token response: {Json}",
                tokenJson[..Math.Min(200, tokenJson.Length)]);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                TempData["TwitterError"] = "Token exchange failed.";
                return RedirectToAction("Index");
            }

            using var tokenDoc = JsonDocument.Parse(tokenJson);
            var tokenRoot = tokenDoc.RootElement;
            var accessToken = tokenRoot.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            var refreshToken = tokenRoot.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            var expiresIn = tokenRoot.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 7200;

            if (string.IsNullOrEmpty(accessToken))
            {
                TempData["TwitterError"] = "Access token not found.";
                return RedirectToAction("Index");
            }

            // Twitter user info lo
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            var userHttpResponse = await client.GetAsync(
            "https://api.twitter.com/2/users/me?user.fields=name,username,profile_image_url");
            var userResponse = await userHttpResponse.Content.ReadAsStringAsync();

            using var userDoc = JsonDocument.Parse(userResponse);
            var userData = userDoc.RootElement.TryGetProperty("data", out var d) ? d : (JsonElement?)null;

            var twitterUserId = userData?.TryGetProperty("id", out var tid) == true ? tid.GetString() : null;
            var twitterName = userData?.TryGetProperty("name", out var tn) == true ? tn.GetString() : null;
            var twitterHandle = userData?.TryGetProperty("username", out var th) == true ? th.GetString() : null;
            var profileImg = userData?.TryGetProperty("profile_image_url", out var pi) == true ? pi.GetString() : null;

            // DB mein save/update karo
            var existing = await _context.TwitterAccounts
                .Where(t => t.UserId == userId)
                .FirstOrDefaultAsync();

            if (existing != null)
            {
                existing.TwitterUserId = twitterUserId!;
                existing.TwitterUsername = twitterHandle ?? "";
                existing.TwitterName = twitterName ?? "";
                existing.ProfileImageUrl = profileImg;
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
                    TwitterUserId = twitterUserId!,
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
             
            TempData["TwitterSuccess"] = $"Twitter connected! @{twitterHandle}";
            return RedirectToAction("Index");
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