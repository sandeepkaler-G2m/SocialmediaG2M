using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    [Table("linkedin_integrations")]
    public class LinkedInIntegration
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string UserId { get; set; } = "";

        [MaxLength(100)]
        public string LinkedInUserId { get; set; } = "";

        [MaxLength(200)]
        public string DisplayName { get; set; } = "";

        [MaxLength(500)]
        public string? ProfilePicture { get; set; }

        [MaxLength(200)]
        public string? Email { get; set; }

        public string? AccessToken { get; set; }

        public string? RefreshToken { get; set; }

        public DateTime? TokenExpiresAt { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;

        public DateTime? DisconnectedAt { get; set; }
    }
}