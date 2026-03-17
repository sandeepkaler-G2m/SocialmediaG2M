namespace SocialMediaPanel.Models
{
    /// <summary>
    /// ViewModel for the Facebook connection modal partial view.
    /// Passed from IntegrationsController → _Facebook.cshtml
    /// </summary>
    public class FacebookConnectViewModel
    {
        // ── Pre-fill / display ───────────────────────────────────────
        /// <summary>Already-connected page name (if reconnecting)</summary>
        public string? ConnectedPageName { get; set; }

        /// <summary>True when the user is reconnecting an existing integration</summary>
        public bool IsReconnecting { get; set; }

        // ── OAuth config passed to the view ─────────────────────────
        /// <summary>
        /// Full Facebook OAuth URL the "Continue with Facebook" button redirects to.
        /// Built by FacebookService.BuildOAuthUrl()
        /// </summary>
        public string OAuthUrl { get; set; } = "#";

        // ── State after coming back from OAuth callback ──────────────
        /// <summary>Set to true when returning from a successful OAuth callback</summary>
        public bool OAuthSuccess { get; set; }

        /// <summary>Error message to show if OAuth failed or was denied</summary>
        public string? OAuthError { get; set; }

        // ── Permissions the app will request ────────────────────────
        public List<FacebookPermission> Permissions { get; set; } = new()
        {
            new() { Icon = "📄", Scope = "pages_manage_posts",   Label = "Manage Pages",       Desc  = "Post and schedule content on your Facebook Pages" },
            new() { Icon = "📊", Scope = "pages_read_engagement", Label = "Read Insights",       Desc  = "Access reach, impressions, and engagement data" },
            new() { Icon = "💬", Scope = "pages_manage_comments", Label = "Moderate Comments",   Desc  = "Reply to, hide, or delete page comments" },
            new() { Icon = "📣", Scope = "ads_management",        Label = "Ads Management",      Desc  = "Optionally boost posts from the panel (can be skipped)" },
        };
    }

    public class FacebookPermission
    {
        public string Icon { get; set; } = "";
        public string Scope { get; set; } = "";
        public string Label { get; set; } = "";
        public string Desc { get; set; } = "";
    }
}
