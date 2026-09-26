using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    // Maps onto an existing (previously unused) table — LinkedInLeads
    // already existed in the live DB with exactly this shape, so no new
    // table was needed for this piece. LinkedIn's leadFormResponses
    // answers are keyed by a generic numeric questionId, not named
    // fields like Meta's "full_name"/"email" — so Name/Email/Phone stay
    // null unless a future form-schema lookup maps question IDs to field
    // types; RawJson always has the full answer set either way.
    [Table("LinkedInLeads")]
    public class LinkedInLead
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string LeadId { get; set; } = ""; // leadFormResponse id (unique)

        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }

        public string? RawJson { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
