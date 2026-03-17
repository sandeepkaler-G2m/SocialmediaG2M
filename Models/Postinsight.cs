using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    [Table("post_insights")]
    public class PostInsight
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        // FB Post ID ya IG Media ID
        [Required]
        [MaxLength(100)]
        [Column("post_id")]
        public string PostId { get; set; } = string.Empty;

        [MaxLength(100)]
        [Column("page_id")]
        public string? PageId { get; set; }

        // "facebook" | "instagram"
        [Required]
        [MaxLength(20)]
        [Column("platform")]
        public string Platform { get; set; } = "facebook";

        [Column("likes_count")]
        public int LikesCount { get; set; } = 0;

        [Column("comments_count")]
        public int CommentsCount { get; set; } = 0;

        [Column("shares_count")]
        public int SharesCount { get; set; } = 0;

        [Column("reach")]
        public int Reach { get; set; } = 0;

        [Column("impressions")]
        public int Impressions { get; set; } = 0;

        [Column("saves_count")]
        public int SavesCount { get; set; } = 0;

        [Column("updated_at")]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
