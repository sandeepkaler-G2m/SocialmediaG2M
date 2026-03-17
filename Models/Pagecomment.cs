using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    [Table("page_comments")]
    public class PageComment
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        [Column("comment_id")]
        public string CommentId { get; set; } = string.Empty;

        [MaxLength(100)]
        [Column("page_id")]
        public string? PageId { get; set; }

        [MaxLength(100)]
        [Column("post_id")]
        public string? PostId { get; set; }

        [MaxLength(100)]
        [Column("sender_id")]
        public string? SenderId { get; set; }

        [MaxLength(200)]
        [Column("sender_name")]
        public string? SenderName { get; set; }

        [Column("message")]
        public string? Message { get; set; }

        // "facebook" | "instagram"
        [Required]
        [MaxLength(20)]
        [Column("platform")]
        public string Platform { get; set; } = "facebook";

        // "comment" | "mention" | "live_comment"
        [MaxLength(30)]
        [Column("comment_type")]
        public string CommentType { get; set; } = "comment";

        [Column("comment_time")]
        public DateTime? CommentTime { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
