using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    /// <summary>
    /// One generated PDF within a <see cref="PdfMergeBatch"/> — corresponds to a
    /// single Excel row. whatsapp_status/whatsapp_message_id are reserved for the
    /// send phase (not built yet) and stay unused for now.
    /// </summary>
    [Table("pdfmerge_records")]
    public class PdfMergeRecord
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("batch_id")]
        public int BatchId { get; set; }

        [Column("row_index")]
        public int RowIndex { get; set; }

        /// <summary>The Excel row's column-&gt;value map, serialized as JSON, for the batch-detail view.</summary>
        [Required]
        [Column("data_json")]
        public string DataJson { get; set; } = "{}";

        [MaxLength(30)]
        [Column("phone_number")]
        public string? PhoneNumber { get; set; }

        [Required]
        [MaxLength(255)]
        [Column("generated_file_name")]
        public string GeneratedFileName { get; set; } = string.Empty;

        [Required]
        [MaxLength(500)]
        [Column("generated_url")]
        public string GeneratedUrl { get; set; } = string.Empty;

        [MaxLength(20)]
        [Column("whatsapp_status")]
        public string? WhatsAppStatus { get; set; }

        [MaxLength(255)]
        [Column("whatsapp_message_id")]
        public string? WhatsAppMessageId { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}
