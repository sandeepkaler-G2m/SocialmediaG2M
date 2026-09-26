using System.ComponentModel.DataAnnotations;

namespace SocialMediaPanel.Models
{
    // Tracks a registered LinkedIn leadNotifications webhook subscription
    // (POST /rest/leadNotifications) so the UI can show what's already
    // registered instead of re-registering blindly.
    public class LinkedInLeadSubscription
    {
        [Key]
        public int Id { get; set; }

        public string UserId { get; set; } = "";
        public int LinkedInIntegrationId { get; set; }

        public string OwnerUrn { get; set; } = ""; // urn:li:organization:{id}
        public string WebhookUrl { get; set; } = "";

        // Id LinkedIn assigned to this subscription (needed to delete it later).
        public string? LinkedInSubscriptionId { get; set; }

        public bool Active { get; set; } = true;
        public string Status { get; set; } = "pending"; // pending | registered | failed
        public string? ErrorMessage { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
