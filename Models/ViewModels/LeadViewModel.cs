using System.ComponentModel.DataAnnotations;

namespace SocialMediaPanel.ViewModels
{
    public class LeadViewModel
    {
        [Key]
        public int Id { get; set; }

        [Display(Name = "Lead ID")]
        public string LeadId { get; set; } = string.Empty;

        [Display(Name = "Page ID")]
        public string? PageId { get; set; }

        [Display(Name = "Form ID")]
        public string? FormId { get; set; }

        [Display(Name = "Full Name")]
        public string? FullName { get; set; }

        [EmailAddress]
        public string? Email { get; set; }

        [Phone]
        public string? Phone { get; set; }

        [Required]
        public string Platform { get; set; } = "facebook";

        public string? RawData { get; set; }

        public string? Status { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    public class UpdateStatusRequest
    {
        public int Id { get; set; }
        public string Status { get; set; } = "open";
    }
}