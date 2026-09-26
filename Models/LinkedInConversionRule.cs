using System.ComponentModel.DataAnnotations;

namespace SocialMediaPanel.Models
{
    // A LinkedIn Conversions API rule (rw_conversions + r_ads) — requires
    // the connected LinkedIn account to have a role on the given ad
    // account. Created once via POST /rest/conversions; conversion events
    // are then streamed against its ConversionUrn.
    public class LinkedInConversionRule
    {
        [Key]
        public int Id { get; set; }

        public string UserId { get; set; } = "";
        public int LinkedInIntegrationId { get; set; }

        [Required]
        public string Name { get; set; } = "";
        public string AdAccountUrn { get; set; } = ""; // urn:li:sponsoredAccount:{id}

        // LEAD | PURCHASE | SIGN_UP | ADD_TO_CART | ... (LinkedIn's ConversionType enum)
        public string ConversionType { get; set; } = "LEAD";

        // Populated once created — urn:lla:llaPartnerConversion:{id}
        public string? ConversionUrn { get; set; }

        public bool Enabled { get; set; } = true;
        public string Status { get; set; } = "draft"; // draft | created | failed
        public string? ErrorMessage { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    // One streamed conversion event — logged regardless of outcome so a
    // failed stream is visible, not silently lost.
    public class LinkedInConversionEvent
    {
        [Key]
        public int Id { get; set; }

        public int ConversionRuleId { get; set; }
        public string EventId { get; set; } = "";
        public decimal? Amount { get; set; }
        public string? CurrencyCode { get; set; }
        public string UserIdentifierType { get; set; } = ""; // SHA256_EMAIL | PLAINTEXT_IP_ADDRESS | ...
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime SentAt { get; set; } = DateTime.UtcNow;
    }
}
