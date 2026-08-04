using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    [Table("TweetsPosted")]
    public class TweetPosted
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        [MaxLength(100)]
        public string? TwitterAccountId { get; set; }

        [MaxLength(100)]
        public string? TweetId { get; set; }

        [Required]
        [MaxLength(280)]
        public string TweetText { get; set; } = string.Empty;

        // "posted" | "failed" | "scheduled"
        [MaxLength(20)]
        public string Status { get; set; } = "posted";

        public string? ErrorMessage { get; set; }

        public DateTime? PostedAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;


        [NotMapped]
        public string? MediaUrl { get; set; }
    }
}