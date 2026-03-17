using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    [Table("webhook_events")]
    public class WebhookEvent
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        // "facebook" | "instagram" | "ad_account"
        [Required]
        [MaxLength(20)]
        [Column("platform")]
        public string Platform { get; set; } = string.Empty;

        // "messages" | "feed" | "leadgen" | "comments" | "mention" etc
        [Required]
        [MaxLength(100)]
        [Column("event_type")]
        public string EventType { get; set; } = string.Empty;

        // Page ID ya IG Account ID
        [MaxLength(100)]
        [Column("object_id")]
        public string? ObjectId { get; set; }

        // Meta se aaya hua full JSON
        [Column("raw_payload")]
        public string RawPayload { get; set; } = string.Empty;

        [Column("processed")]
        public bool Processed { get; set; } = false;

        [Column("received_at")]
        public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    }
}
