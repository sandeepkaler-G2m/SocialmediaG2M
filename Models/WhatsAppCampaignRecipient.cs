using System.ComponentModel.DataAnnotations;

namespace SocialMediaPanel.Models
{
    // One row per phone number in a WhatsAppCampaign — tracks its own
    // send outcome independently so a partial-failure campaign still shows
    // exactly who succeeded and who didn't (and why).
    public class WhatsAppCampaignRecipient
    {
        [Key]
        public int Id { get; set; }

        public int CampaignId { get; set; }

        [Required]
        public string PhoneNumber { get; set; } = "";

        // pending | sent | failed
        public string Status { get; set; } = "pending";

        public string? MessageId { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime? SentAt { get; set; }

        // Comma-separated, per-recipient template body param values —
        // resolved at creation time from either a mapped CSV column or a
        // static value entered once for all rows. Null for non-template
        // campaigns or when the template takes no variables.
        public string? VariableValues { get; set; }
    }
}
