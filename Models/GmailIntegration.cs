using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    /// <summary>
    /// Stores Gmail OAuth tokens after a user connects their Gmail account.
    /// One row per connected Gmail account per user.
    ///
    /// Add to AppDbContext:
    ///     public DbSet&lt;GmailIntegration&gt; GmailIntegrations { get; set; }
    ///
    /// Then run:
    ///     Add-Migration AddGmailIntegration
    ///     Update-Database
    /// </summary>
    [Table("gmail_integrations")]
    public class GmailIntegration
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        // ── Which user connected this ────────────────────────────────
        [Required]
        [MaxLength(200)]
        [Column("user_id")]
        public string UserId { get; set; } = string.Empty;

        // ── Google account info ──────────────────────────────────────
        [MaxLength(200)]
        [Column("google_account_id")]
        public string GoogleAccountId { get; set; } = string.Empty;

        [MaxLength(200)]
        [Column("email_address")]
        public string EmailAddress { get; set; } = string.Empty;

        [MaxLength(200)]
        [Column("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [MaxLength(500)]
        [Column("profile_picture")]
        public string ProfilePicture { get; set; } = string.Empty;

        // ── OAuth tokens ─────────────────────────────────────────────
        [Required]
        [Column("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [Column("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;

        [Column("token_expires_at")]
        public DateTime TokenExpiresAt { get; set; }

        // ── Scopes granted ───────────────────────────────────────────
        [Column("granted_scopes")]
        public string GrantedScopes { get; set; } = string.Empty;

        // ── Status ───────────────────────────────────────────────────
        [Column("is_active")]
        public bool IsActive { get; set; } = true;

        [Column("connected_at")]
        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;

        [Column("disconnected_at")]
        public DateTime? DisconnectedAt { get; set; }

        [Column("last_sync_at")]
        public DateTime? LastSyncAt { get; set; }

        [MaxLength(1000)]
        [Column("last_error")]
        public string? LastError { get; set; }
    }
}