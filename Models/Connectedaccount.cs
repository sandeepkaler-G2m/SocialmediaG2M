using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    [Table("connected_accounts")]
    public class ConnectedAccount
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("user_id")]
        public int UserId { get; set; }

        // "facebook" | "instagram"
        [Required]
        [MaxLength(20)]
        [Column("platform")]
        public string Platform { get; set; } = string.Empty;

        // FB Page ID ya IG Account ID
        [Required]
        [MaxLength(100)]
        [Column("account_id")]
        public string AccountId { get; set; } = string.Empty;

        [Required]
        [MaxLength(200)]
        [Column("account_name")]
        public string AccountName { get; set; } = string.Empty;

        [Column("access_token")]
        public string? AccessToken { get; set; }

        [Column("token_expires_at")]
        public DateTime? TokenExpiresAt { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; } = true;

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
