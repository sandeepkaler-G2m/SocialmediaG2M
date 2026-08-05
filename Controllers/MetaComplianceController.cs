using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SocialMediaPanel.Controllers
{
    /// <summary>
    /// The two endpoints Meta requires before it will approve any permission
    /// in App Review: a Deauthorize Callback (fired when a user removes the
    /// app) and a Data Deletion Request callback (fired when a user asks
    /// Meta to have their data erased). Both receive a signed POST — a
    /// "signed_request" — that must be verified with the app's secret before
    /// trusting the user_id inside it.
    /// </summary>
    [Route("meta")]
    [ApiController]
    public class MetaComplianceController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;
        private readonly ILogger<MetaComplianceController> _logger;

        public MetaComplianceController(AppDbContext db, IConfiguration config, ILogger<MetaComplianceController> logger)
        {
            _db = db;
            _config = config;
            _logger = logger;
        }

        // ══════════════════════════════════════════════════════
        // DEAUTHORIZE CALLBACK
        // Meta POSTs here when a user removes the app from their
        // Facebook/Instagram settings. A 200 response is all Meta requires;
        // we use it to drop that person's stored connection rows.
        // ══════════════════════════════════════════════════════
        [HttpPost("deauthorize")]
        public async Task<IActionResult> Deauthorize()
        {
            string body;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            var form = System.Web.HttpUtility.ParseQueryString(body);
            var signedRequest = form["signed_request"];

            var parsed = ParseSignedRequest(signedRequest);
            if (parsed == null)
            {
                _logger.LogWarning("Deauthorize callback: invalid signed_request");
                return Ok(); // still 200 — Meta doesn't need an error body here
            }

            _logger.LogInformation("Deauthorize callback for Meta user_id={UserId}", parsed.Value.UserId);

            // Best-effort cleanup: drop any connection rows tied to this Meta
            // user_id across both Facebook Pages and native Instagram accounts.
            var fbRows = await _db.FacebookPages.Where(p => p.user_name == parsed.Value.UserId).ToListAsync();
            if (fbRows.Count > 0) _db.FacebookPages.RemoveRange(fbRows);

            var igRows = await _db.InstagramAccounts.Where(a => a.InstagramUserId == parsed.Value.UserId).ToListAsync();
            if (igRows.Count > 0) _db.InstagramAccounts.RemoveRange(igRows);

            await _db.SaveChangesAsync();

            return Ok();
        }

        // ══════════════════════════════════════════════════════
        // DATA DELETION REQUEST
        // Meta requires a JSON response with a status-check URL and a
        // confirmation code — the user is redirected to that URL to see
        // their deletion was actioned.
        // ══════════════════════════════════════════════════════
        [HttpPost("data-deletion")]
        public async Task<IActionResult> DataDeletion()
        {
            string body;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            var form = System.Web.HttpUtility.ParseQueryString(body);
            var signedRequest = form["signed_request"];

            var parsed = ParseSignedRequest(signedRequest);
            var confirmationCode = Guid.NewGuid().ToString("N").Substring(0, 12);

            if (parsed != null)
            {
                _logger.LogInformation("Data deletion request for Meta user_id={UserId}, code={Code}", parsed.Value.UserId, confirmationCode);

                var fbRows = await _db.FacebookPages.Where(p => p.user_name == parsed.Value.UserId).ToListAsync();
                if (fbRows.Count > 0) _db.FacebookPages.RemoveRange(fbRows);

                var igRows = await _db.InstagramAccounts.Where(a => a.InstagramUserId == parsed.Value.UserId).ToListAsync();
                if (igRows.Count > 0) _db.InstagramAccounts.RemoveRange(igRows);

                await _db.SaveChangesAsync();
            }
            else
            {
                _logger.LogWarning("Data deletion callback: invalid signed_request, code={Code}", confirmationCode);
            }

            var baseUrl = _config["AppBaseUrl"]?.TrimEnd('/') ?? $"{Request.Scheme}://{Request.Host}";
            return Ok(new
            {
                url = $"{baseUrl}/meta/data-deletion/status/{confirmationCode}",
                confirmation_code = confirmationCode
            });
        }

        [HttpGet("data-deletion/status/{code}")]
        public IActionResult DataDeletionStatus(string code)
        {
            return Content(
                $"<html><body style='font-family:sans-serif;padding:40px;text-align:center'>" +
                $"<h2>Data Deletion Completed</h2>" +
                $"<p>Confirmation code: <b>{System.Net.WebUtility.HtmlEncode(code)}</b></p>" +
                $"<p>All data associated with your account has been removed from SocialMediaPanel.</p>" +
                $"</body></html>", "text/html");
        }

        // ── signed_request parser + verifier ────────────────────────────
        // Tries both the Facebook app secret and the Instagram app secret,
        // since either integration's product can be the one that fired this.
        private (string UserId, JsonElement Payload)? ParseSignedRequest(string? signedRequest)
        {
            if (string.IsNullOrEmpty(signedRequest)) return null;
            var parts = signedRequest.Split('.');
            if (parts.Length != 2) return null;

            var encodedSig = parts[0];
            var payloadRaw = parts[1];

            byte[] sig;
            string payloadJson;
            try
            {
                sig = Base64UrlDecode(encodedSig);
                payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(payloadRaw));
            }
            catch { return null; }

            var secrets = new[] { _config["Facebook:AppSecret"], _config["Instagram:AppSecret"] };
            bool verified = false;
            foreach (var secret in secrets)
            {
                if (string.IsNullOrEmpty(secret)) continue;
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadRaw));
                if (CryptographicOperations.FixedTimeEquals(expected, sig))
                {
                    verified = true;
                    break;
                }
            }

            if (!verified) return null;

            using var doc = JsonDocument.Parse(payloadJson);
            var userId = doc.RootElement.TryGetProperty("user_id", out var uid) ? uid.GetString() : null;
            if (string.IsNullOrEmpty(userId)) return null;

            return (userId, doc.RootElement);
        }

        private static byte[] Base64UrlDecode(string input)
        {
            var s = input.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }
    }
}
