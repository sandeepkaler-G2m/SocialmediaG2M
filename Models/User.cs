using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    [Table("usersinfo")]
    public class User
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        [Column("name")]
        public string Name { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [MaxLength(150)]
        [Column("email")]
        public string Email { get; set; } = string.Empty;

        [Required]
        [MaxLength(255)]
        [Column("password")]
        public string Password { get; set; } = string.Empty;

        [MaxLength(200)]
        [Column("company_name")]
        public string? CompanyName { get; set; }

        [MaxLength(50)]
        [Column("company_size")]
        public string? CompanySize { get; set; }

        [MaxLength(100)]
        [Column("company_type")]
        public string? CompanyType { get; set; }

        [Column("company_address")]
        public string? CompanyAddress { get; set; }
    }
}