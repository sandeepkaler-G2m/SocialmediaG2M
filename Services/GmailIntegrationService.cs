using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;

namespace SocialMediaPanel.Services
{
    /// <summary>
    /// Manages Gmail integration lifecycle + all actions you can perform:
    ///
    ///   SaveAsync()            — persist tokens after OAuth
    ///   GetAsync()             — load active integration for a user
    ///   GetEmailsAsync()       — fetch inbox emails
    ///   SendEmailAsync()       — send a new email
    ///   ReplyAsync()           — reply to an email thread
    ///   GetLabelsAsync()       — list Gmail labels
    ///   ApplyLabelAsync()      — label a message
    ///   SearchAsync()          — search emails by query
    ///   GetUnreadCountAsync()  — unread badge count
    ///   EnsureFreshTokenAsync()— auto-refresh expired access token
    ///   DisconnectAsync()      — revoke + mark inactive
    /// </summary>
    public class GmailIntegrationService
    {
        private readonly AppDbContext _db;
        private readonly GmailService _gmail;

        public GmailIntegrationService(AppDbContext db, GmailService gmail)
        {
            _db = db;
            _gmail = gmail;
        }

        // ── Save after OAuth ──────────────────────────────────────────
        public async Task SaveAsync(
            string userId,
            GmailTokenResult tokenResult,
            GmailProfile profile)
        {
            var existing = await _db.GmailIntegrations
                .FirstOrDefaultAsync(g => g.UserId == userId
                                       && g.GoogleAccountId == profile.GoogleId);

            if (existing != null)
            {
                // Update — token refresh or reconnect
                existing.AccessToken = tokenResult.AccessToken;
                existing.RefreshToken = string.IsNullOrEmpty(tokenResult.RefreshToken)
                                             ? existing.RefreshToken   // keep old if not re-issued
                                             : tokenResult.RefreshToken;
                existing.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenResult.ExpiresIn);
                existing.GrantedScopes = tokenResult.Scope;
                existing.DisplayName = profile.DisplayName;
                existing.ProfilePicture = profile.PictureUrl;
                existing.IsActive = true;
                existing.DisconnectedAt = null;
                existing.LastError = null;
            }
            else
            {
                _db.GmailIntegrations.Add(new GmailIntegration
                {
                    UserId = userId,
                    GoogleAccountId = profile.GoogleId,
                    EmailAddress = profile.Email,
                    DisplayName = profile.DisplayName,
                    ProfilePicture = profile.PictureUrl,
                    AccessToken = tokenResult.AccessToken,
                    RefreshToken = tokenResult.RefreshToken,
                    TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenResult.ExpiresIn),
                    GrantedScopes = tokenResult.Scope,
                    IsActive = true,
                    ConnectedAt = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync();
        }

        // ── Get active integration ────────────────────────────────────
        public async Task<GmailIntegration?> GetAsync(string userId)
        {
            return await _db.GmailIntegrations
                .Where(g => g.UserId.Trim() == userId.Trim() && g.IsActive == true)
                .OrderByDescending(g => g.ConnectedAt)
                .FirstOrDefaultAsync();
        }

        // ── Auto-refresh token if expired ────────────────────────────
        public async Task<string> EnsureFreshTokenAsync(GmailIntegration? integration)
        {
            if (integration == null)
                throw new InvalidOperationException("No active Gmail integration.");

            // Token still valid (with 60s buffer)
            if (integration.TokenExpiresAt > DateTime.UtcNow.AddSeconds(60))
                return integration.AccessToken;

            // Refresh it
            var newToken = await _gmail.RefreshAccessTokenAsync(integration.RefreshToken);
            integration.AccessToken = newToken;
            integration.TokenExpiresAt = DateTime.UtcNow.AddSeconds(3500);
            integration.LastSyncAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return newToken;
        }

        // ── Get emails ────────────────────────────────────────────────
        public async Task<List<GmailMessage>> GetEmailsAsync(
            string userId,
            int maxResults = 20,
            string query = "")
        {
            var integ = await _GetOrThrow(userId);
            var token = await EnsureFreshTokenAsync(integ);
            return await _gmail.GetEmailsAsync(token, maxResults, query);
        }

        // ── Get email body ────────────────────────────────────────────
        public async Task<string> GetEmailBodyAsync(string userId, string messageId)
        {
            var integ = await _GetOrThrow(userId);
            var token = await EnsureFreshTokenAsync(integ);
            return await _gmail.GetEmailBodyAsync(token, messageId);
        }

        // ── Send email ────────────────────────────────────────────────
        public async Task<string> SendEmailAsync(
            string userId,
            string to,
            string subject,
            string bodyHtml)
        {
            var integ = await _GetOrThrow(userId);
            var token = await EnsureFreshTokenAsync(integ);
            var msgId = await _gmail.SendEmailAsync(token, to, subject, bodyHtml);
            integ.LastSyncAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return msgId;
        }

        // ── Reply to email ────────────────────────────────────────────
        public async Task<string> ReplyAsync(
            string userId,
            string threadId,
            string messageId,
            string to,
            string subject,
            string bodyHtml)
        {
            var integ = await _GetOrThrow(userId);
            var token = await EnsureFreshTokenAsync(integ);
            return await _gmail.ReplyToEmailAsync(token, threadId, messageId, to, subject, bodyHtml);
        }

        // ── Get labels ────────────────────────────────────────────────
        public async Task<List<GmailLabel>> GetLabelsAsync(string userId)
        {
            var integ = await _GetOrThrow(userId);
            var token = await EnsureFreshTokenAsync(integ);
            return await _gmail.GetLabelsAsync(token);
        }

        // ── Apply label ───────────────────────────────────────────────
        public async Task ApplyLabelAsync(string userId, string messageId, string labelId)
        {
            var integ = await _GetOrThrow(userId);
            var token = await EnsureFreshTokenAsync(integ);
            await _gmail.ApplyLabelAsync(token, messageId, labelId);
        }

        // ── Search emails ─────────────────────────────────────────────
        public async Task<List<GmailMessage>> SearchAsync(
            string userId,
            string query,
            int maxResults = 20)
        {
            var integ = await _GetOrThrow(userId);
            var token = await EnsureFreshTokenAsync(integ);
            return await _gmail.SearchEmailsAsync(token, query, maxResults);
        }

        // ── Unread count ──────────────────────────────────────────────
        public async Task<int> GetUnreadCountAsync(string userId)
        {
            var integ = await _GetOrThrow(userId);
            var token = await EnsureFreshTokenAsync(integ);
            return await _gmail.GetUnreadCountAsync(token);
        }

        // ── Disconnect ────────────────────────────────────────────────
        public async Task DisconnectAsync(string userId)
        {
            var integrations = await _db.GmailIntegrations
                .Where(g => g.UserId == userId && g.IsActive)
                .ToListAsync();

            foreach (var g in integrations)
            {
                try { await _gmail.RevokeTokenAsync(g.AccessToken); } catch { }
                g.IsActive = false;
                g.DisconnectedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();
        }

        // ── Is token expiring soon ────────────────────────────────────
        public async Task<bool> IsTokenExpiringSoonAsync(string userId)
        {
            var integ = await GetAsync(userId);
            if (integ == null) return false;
            return (integ.TokenExpiresAt - DateTime.UtcNow).TotalDays < 7;
        }

        private async Task<GmailIntegration> _GetOrThrow(string userId)
        {
            var integ = await GetAsync(userId);
            if (integ == null)
                throw new InvalidOperationException("No active Gmail integration found. Please connect Gmail first.");
            return integ;
        }
    }
}