using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    /// <summary>
    /// Login account for the standalone WhatsApp-PDF Mail-Merge feature
    /// (Areas/WhatsAppPdf). Deliberately separate from the main app's
    /// <see cref="User"/> table — own credentials, own role.
    /// </summary>
    [Table("pdfmerge_users")]
    public class PdfMergeUser
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        [Column("username")]
        public string Username { get; set; } = string.Empty;

        [Required]
        [MaxLength(255)]
        [Column("password_hash")]
        public string PasswordHash { get; set; } = string.Empty;

        [Required]
        [MaxLength(20)]
        [Column("role")]
        public string Role { get; set; } = "user";

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}
