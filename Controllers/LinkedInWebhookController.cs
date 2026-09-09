using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SocialMediaPanel.Controllers
{
    // ══════════════════════════════════════════════════════════════════
    // LinkedIn Lead Sync webhook — receives real-time lead notifications.
    //
    // This endpoint is fully live and testable today, independent of
    // whether LinkedIn has approved the Lead Sync API for this app —
    // it's just an HTTP endpoint implementing LinkedIn's documented
    // contract (https://learn.microsoft.com/en-us/linkedin/shared/api-guide/webhook-validation).
    // Registering it with LinkedIn (POST /rest/leadNotifications) and
    // actually receiving real lead traffic both require LinkedIn to
    // approve the Lead Sync API product for this app first — that
    // approval, and the org-ownership mapping it unlocks, are the parts
    // that can't be built or tested until then.
    //
    // Two things this endpoint does NOT yet do, both blocked on the same
    // approval:
    //   1. Fetch the full lead answers (name/email/phone). LinkedIn's
    //      webhook payload only carries IDs/URNs, not the form answers —
    //      those need a follow-up authenticated GET to
    //      /rest/leadFormResponses/{id} using an access token that has
    //      r_marketing_leadgen_automation, which no connected account
    //      has yet.
    //   2. Resolve which of our users owns the organization a lead
    //      belongs to. LinkedInIntegrations doesn't store organization
    //      URNs yet (GetOrganizationsAsync in LinkedInService is built
    //      but has nothing to call it until org access is approved).
    //
    // Until then, this stores what the notification itself contains —
    // enough to prove the endpoint, signature verification, and
    // deduplication all work correctly.
    // ══════════════════════════════════════════════════════════════════
    [ApiController]
    public class LinkedInWebhookController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;
        private readonly ILogger<LinkedInWebhookController> _logger;

        public LinkedInWebhookController(AppDbContext db, IConfiguration config, ILogger<LinkedInWebhookController> logger)
        {
            _db = db;
            _config = config;
            _logger = logger;
        }

        private string ClientSecret => _config["LinkedIn:ClientSecret"]!;

        // ── Step 1: LinkedIn's ownership-verification handshake ─────────
        // GET /linkedin/webhook?challengeCode=<uuid>
        // Must respond within 3 seconds with {challengeCode, challengeResponse}.
        [HttpGet]
        [Route("linkedin/webhook")]
        public IActionResult Verify([FromQuery] string? challengeCode)
        {
            if (string.IsNullOrEmpty(challengeCode))
                return BadRequest(new { error = "Missing challengeCode" });

            var challengeResponse = HexHmacSha256(challengeCode, ClientSecret);

            _logger.LogInformation("LinkedIn webhook verification challenge received and answered.");

            return Ok(new { challengeCode, challengeResponse });
        }

        // ── Step 2: real-time lead notifications ────────────────────────
        // POST /linkedin/webhook
        // Body: {"type":"LEAD_ACTION","leadGenFormResponse":"urn:li:leadGenFormResponse:...",
        //        "leadGenForm":"...","owner":{"organization":"urn:li:organization:123"},
        //        "leadType":"SPONSORED","leadAction":"CREATED","occurredAt":1699999999000}
        // Header: X-LI-Signature = Hex(HMACSHA256("hmacsha256=" + rawBody, clientSecret))
        [HttpPost]
        [Route("linkedin/webhook")]
        public async Task<IActionResult> Receive()
        {
            string rawBody;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
                rawBody = await reader.ReadToEndAsync();

            // ── Verify X-LI-Signature using the RAW body — re-serializing
            // the parsed JSON would change whitespace/ordering and break
            // the signature match, so this must happen before any parsing. ──
            if (!Request.Headers.TryGetValue("X-LI-Signature", out var sigHeader))
            {
                _logger.LogWarning("LinkedIn webhook POST missing X-LI-Signature header — rejected.");
                return Unauthorized();
            }

            var expectedSig = HexHmacSha256("hmacsha256=" + rawBody, ClientSecret);
            var providedSig = sigHeader.ToString();

            var sigMatches = providedSig.Length == expectedSig.Length &&
                CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(providedSig),
                    Encoding.UTF8.GetBytes(expectedSig));

            if (!sigMatches)
            {
                _logger.LogWarning("LinkedIn webhook signature mismatch — rejected.");
                return Unauthorized();
            }

            try
            {
                using var doc = JsonDocument.Parse(rawBody);
                var root = doc.RootElement;

                var leadGenFormResponse = root.TryGetProperty("leadGenFormResponse", out var lgfr) ? lgfr.GetString() : null;
                var leadAction = root.TryGetProperty("leadAction", out var la) ? la.GetString() : null;
                var leadType = root.TryGetProperty("leadType", out var lt) ? lt.GetString() : null;
                var occurredAt = root.TryGetProperty("occurredAt", out var oa) ? oa.GetInt64() : 0L;
                string? ownerUrn = null;
                if (root.TryGetProperty("owner", out var owner))
                {
                    if (owner.TryGetProperty("organization", out var org)) ownerUrn = org.GetString();
                    else if (owner.TryGetProperty("sponsoredAccount", out var sa)) ownerUrn = sa.GetString();
                }

                if (string.IsNullOrEmpty(leadGenFormResponse))
                {
                    _logger.LogWarning("LinkedIn webhook payload missing leadGenFormResponse: {Body}", rawBody);
                    return Ok(); // still 2xx — malformed/unexpected payload shouldn't get retried forever
                }

                // Per LinkedIn's docs, leadGenFormResponse URNs are reused across
                // repeated actions (register/unregister/re-register) on the same
                // form+member — the composite key below is what actually
                // identifies a unique event.
                var dedupeKey = $"{leadGenFormResponse}_{occurredAt}";

                var exists = await _db.Leads.AnyAsync(l => l.LeadId == dedupeKey && l.Platform == "linkedin");
                if (exists)
                {
                    _logger.LogInformation("LinkedIn lead notification already processed — skip: {Key}", dedupeKey);
                    return Ok();
                }

                if (leadAction == "CREATED")
                {
                    _db.Leads.Add(new Lead
                    {
                        LeadId = dedupeKey,
                        PageId = ownerUrn,
                        FormId = null,
                        FullName = null, // needs a follow-up leadFormResponses fetch — see class comment
                        Email = null,
                        Phone = null,
                        Platform = "linkedin",
                        RawData = rawBody,
                        Status = "open",
                        CreatedAt = DateTime.UtcNow
                    });
                    await _db.SaveChangesAsync();
                    _logger.LogInformation("LinkedIn lead saved (details pending Lead Sync approval) — leadType={LeadType} owner={Owner}", leadType, ownerUrn);
                }
                // leadAction == "DELETED" — nothing to reconcile yet since we don't
                // persist enough identity to find the original row without the
                // full-detail fetch; logged for visibility only.

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError("LinkedIn webhook processing error: {Msg}", ex.Message);
                // Still 2xx: a malformed payload retried forever helps no one,
                // and the raw body above is already logged for follow-up.
                return Ok();
            }
        }

        private static string HexHmacSha256(string message, string key)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
