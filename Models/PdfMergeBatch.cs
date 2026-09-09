using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    /// <summary>
    /// One Excel + sample-PDF upload -> Generate action for the WhatsApp-PDF
    /// Mail-Merge feature. Has many <see cref="PdfMergeRecord"/> (one per Excel row).
    /// </summary>
    [Table("pdfmerge_batches")]
    public class PdfMergeBatch
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("user_id")]
        public int UserId { get; set; }

        [Required]
        [MaxLength(255)]
        [Column("excel_file_name")]
        public string ExcelFileName { get; set; } = string.Empty;

        [Required]
        [MaxLength(255)]
        [Column("sample_pdf_file_name")]
        public string SamplePdfFileName { get; set; } = string.Empty;

        [MaxLength(100)]
        [Column("phone_column")]
        public string? PhoneColumn { get; set; }

        [Column("total_records")]
        public int TotalRecords { get; set; }

        [Required]
        [MaxLength(20)]
        [Column("status")]
        public string Status { get; set; } = "completed";

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}
