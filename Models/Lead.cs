using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    [Table("leads")]
    public class Lead
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        // Meta ka Lead ID
        [Required]
        [MaxLength(100)]
        [Column("lead_id")]
        public string LeadId { get; set; } = string.Empty;

        [MaxLength(100)]
        [Column("page_id")]
        public string? PageId { get; set; }

        // Lead Ad Form ID
        [MaxLength(100)]
        [Column("form_id")]
        public string? FormId { get; set; }

        [MaxLength(200)]
        [Column("full_name")]
        public string? FullName { get; set; }

        [MaxLength(150)]
        [Column("email")]
        public string? Email { get; set; }

        [MaxLength(50)]
        [Column("phone")]
        public string? Phone { get; set; }

        // "facebook" | "instagram" | "facebook_ad"
        [Required]
        [MaxLength(20)]
        [Column("platform")]
        public string Platform { get; set; } = "facebook";

        // Meta se aaya hua full lead JSON
        [Column("raw_data")]
        public string? RawData { get; set; }

        public string? Status { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
