namespace SocialMediaPanel.Services
{
    /// <summary>
    /// Single source of truth for the Facebook/Instagram OAuth permission set.
    /// Both FacebookService.BuildOAuthUrl and InstagramService.BuildOAuthUrl request
    /// this same full list, so whichever "Connect" button the user clicks, the
    /// resulting token carries every permission the app needs (posting, comments,
    /// messaging, insights, leads, ads) — nothing is left out because the user
    /// happened to connect via the "other" button.
    /// </summary>
    public static class MetaScopes
    {
        // Note: comment moderation (reply/hide/delete) is covered by
        // pages_manage_engagement — there is no separate "pages_manage_comments"
        // permission; Facebook's OAuth dialog rejects the whole request if it's
        // included ("Invalid Scope: pages_manage_comments").
        public const string Full =
            "public_profile,email," +
            "pages_show_list," +
            "pages_read_engagement," +
            "pages_manage_metadata," +
            "pages_manage_posts," +
            "pages_manage_engagement," +
            "pages_read_user_content," +
            "pages_messaging," +
            "leads_retrieval," +
            "ads_management," +
            "instagram_basic," +
            "instagram_manage_comments," +
            "instagram_manage_messages," +
            "instagram_content_publish," +
            "instagram_manage_insights";
    }
}
