using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    [Table("page_messages")]
    public class PageMessage
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        [Column("message_id")]
        public string MessageId { get; set; } = string.Empty;

        // FB Page ID ya IG Account ID
        [MaxLength(100)]
        [Column("page_id")]
        public string? PageId { get; set; }

        // Sender ka Meta User ID
        [MaxLength(100)]
        [Column("sender_id")]
        public string? SenderId { get; set; }

        [MaxLength(200)]
        [Column("sender_name")]
        public string? SenderName { get; set; }

        [Column("message_text")]
        public string? MessageText { get; set; }

        // "messenger" | "instagram_dm"
        [Required]
        [MaxLength(30)]
        [Column("platform")]
        public string Platform { get; set; } = "messenger";

        // Message ka actual timestamp Meta se
        [Column("message_time")]
        public DateTime? MessageTime { get; set; }

        // Reply bheja ya nahi
        [Column("is_replied")]
        public bool IsReplied { get; set; } = false;

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
