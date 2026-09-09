using Microsoft.AspNetCore.Http;

namespace SocialMediaPanel.Services.PdfMerge
{
    /// <summary>
    /// Standalone auth helper for the WhatsApp-PDF Mail-Merge feature (Areas/WhatsAppPdf).
    /// Deliberately separate from the main app's login (Controllers/AccountController) —
    /// own session-key namespace, own password hashing (BCrypt, salted+adaptive — a
    /// step up from the main app's unsalted SHA256, no compatibility reason to match it
    /// here since this is a brand-new table).
    /// </summary>
    public class PdfMergeAuthService
    {
        public const string SessionUserId = "WaPdf_UserId";
        public const string SessionUsername = "WaPdf_Username";
        public const string SessionRole = "WaPdf_Role";

        public string HashPassword(string plainPassword) => BCrypt.Net.BCrypt.HashPassword(plainPassword);

        public bool VerifyPassword(string plainPassword, string hash) => BCrypt.Net.BCrypt.Verify(plainPassword, hash);

        public void SignIn(ISession session, Models.PdfMergeUser user)
        {
            session.SetInt32(SessionUserId, user.Id);
            session.SetString(SessionUsername, user.Username);
            session.SetString(SessionRole, user.Role);
        }

        public void SignOut(ISession session)
        {
            session.Remove(SessionUserId);
            session.Remove(SessionUsername);
            session.Remove(SessionRole);
        }

        public int? GetUserId(ISession session) => session.GetInt32(SessionUserId);

        public bool IsAdmin(ISession session) => session.GetString(SessionRole) == "admin";
    }
}
