using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    // Real WhatsApp Business Cloud API (Meta's official product) — distinct
    // from Services/WhatsAppSendService.cs, which is a separate go2market.ai
    // relay used only by the WhatsApp-PDF Mail-Merge side feature. This one
    // talks to graph.facebook.com directly using a self-service, per-user
    // set of credentials entered via the WhatsApp page's UI (no OAuth flow —
    // WhatsApp Business API doesn't have a per-user consent screen the way
    // Facebook/Instagram/Twitter/LinkedIn do; a business generates its own
    // Phone Number ID + permanent access token in Meta Business Manager and
    // pastes them in here).
    [Table("whatsapp_integrations")]
    public class WhatsAppIntegration
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Required]
        [Column("user_id")]
        public int UserId { get; set; }

        [Required]
        [MaxLength(100)]
        [Column("phone_number_id")]
        public string PhoneNumberId { get; set; } = "";

        [MaxLength(100)]
        [Column("waba_id")]
        public string? WabaId { get; set; }

        [Required]
        [Column("access_token")]
        public string AccessToken { get; set; } = "";

        [MaxLength(30)]
        [Column("display_phone_number")]
        public string? DisplayPhoneNumber { get; set; }

        [MaxLength(200)]
        [Column("verified_name")]
        public string? VerifiedName { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; } = true;

        [Column("connected_at")]
        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    }
}
