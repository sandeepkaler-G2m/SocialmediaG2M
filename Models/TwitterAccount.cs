using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    [Table("TwitterAccounts")]
    public class TwitterAccount
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        [Required]
        [MaxLength(100)]
        public string? TwitterUserId { get; set; } // Make nullable

        [MaxLength(100)]
        public string TwitterUsername { get; set; } = string.Empty;

        [MaxLength(200)]
        public string TwitterName { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? ProfileImageUrl { get; set; }

        public string? AccessToken { get; set; }

        public string? RefreshToken { get; set; }

        public DateTime? TokenExpiresAt { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }
    }
}