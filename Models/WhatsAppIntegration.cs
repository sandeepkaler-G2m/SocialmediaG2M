using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    // WhatsApp Business integration — connected via G2M's own WhatsApp API
    // layer (not a direct-to-Meta OAuth flow). A user pastes their Phone
    // Number ID + Access Token plus a Username/Password/User ID that G2M's
    // own API uses to authenticate/route the connection on its side. Distinct
    // from Services/WhatsAppSendService.cs, which is a separate go2market.ai
    // relay used only by the WhatsApp-PDF Mail-Merge side feature.
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

        // ── G2M's own WhatsApp API credentials ──────────────────────────
        [MaxLength(200)]
        [Column("username")]
        public string? Username { get; set; }

        // Stored encrypted (ASP.NET Core Data Protection) — never plaintext.
        // Needs to be reversible (not a one-way hash) since G2M's API needs
        // the real password on every call, not just a login check.
        [MaxLength(1000)]
        [Column("password")]
        public string? PasswordEncrypted { get; set; }

        [MaxLength(200)]
        [Column("user_id_field")]
        public string? G2MUserId { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; } = true;

        // A user can connect multiple numbers — exactly one is the
        // dashboard's default at a time (see WhatsAppController.SetDefault).
        [Column("is_default")]
        public bool IsDefault { get; set; } = false;

        [Column("connected_at")]
        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    }
}
