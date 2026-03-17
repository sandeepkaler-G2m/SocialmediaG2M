namespace SocialMediaPanel.Models
{
    /// <summary>
    /// Describes one social platform's connection config.
    /// Used by _PlatformAuthModal partial view.
    /// To add a new platform: add one entry to PlatformRegistry.All — nothing else changes.
    /// </summary>
    public class PlatformConfig
    {
        public string Key { get; set; } = "";   // e.g. "facebook"
        public string Name { get; set; } = "";   // e.g. "Facebook"
        public string Color { get; set; } = "";   // hex e.g. "#1877F2"
        public string LogoSvg { get; set; } = "";   // raw inline SVG string
        public string Description { get; set; } = "";
        public string SuccessMsg { get; set; } = "";
        public string Note { get; set; } = "";   // optional warning note

        /// <summary>"oauth" or "apikey"</summary>
        public string AuthType { get; set; } = "oauth";

        /// <summary>Label on the OAuth button (only used when AuthType == "oauth")</summary>
        public string OAuthLabel { get; set; } = "Sign in";

        /// <summary>Input fields shown when AuthType == "apikey"</summary>
        public List<ApiField> Fields { get; set; } = new();

        /// <summary>Permission items shown on step 2 for all platforms</summary>
        public List<Permission> Permissions { get; set; } = new();
    }

    public class ApiField
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Type { get; set; } = "text";   // "text" | "password"
        public string Placeholder { get; set; } = "";
        public string Hint { get; set; } = "";
    }

    public class Permission
    {
        public string Icon { get; set; } = "";
        public string Label { get; set; } = "";
        public string Desc { get; set; } = "";
    }

    /// <summary>
    /// Single place to register all supported platforms.
    /// _PlatformAuthModal.cshtml reads PlatformRegistry.All at render time.
    /// </summary>
    public static class PlatformRegistry
    {
        public static readonly IReadOnlyList<PlatformConfig> All = new List<PlatformConfig>
        {
            /* ── LinkedIn ──────────────────────────────────────────── */
            new PlatformConfig
            {
                Key         = "linkedin",
                Name        = "LinkedIn",
                Color       = "#0A66C2",
                AuthType    = "oauth",
                OAuthLabel  = "Sign in with LinkedIn",
                Description = "Authorise SocialMediaPanel to manage your LinkedIn Pages, post content, and view analytics.",
                LogoSvg     = @"<svg viewBox='0 0 24 24' fill='#0A66C2'><path d='M20.447 20.452h-3.554v-5.569c0-1.328-.027-3.037-1.852-3.037-1.853 0-2.136 1.445-2.136 2.939v5.667H9.351V9h3.414v1.561h.046c.477-.9 1.637-1.85 3.37-1.85 3.601 0 4.267 2.37 4.267 5.455v6.286zM5.337 7.433a2.062 2.062 0 0 1-2.063-2.065 2.064 2.064 0 1 1 2.063 2.065zm1.782 13.019H3.555V9h3.564v11.452zM22.225 0H1.771C.792 0 0 .774 0 1.729v20.542C0 23.227.792 24 1.771 24h20.451C23.2 24 24 23.227 24 22.271V1.729C24 .774 23.2 0 22.222 0h.003z'/></svg>",
                SuccessMsg  = "Your LinkedIn Page is now connected. Posts will appear in your publishing queue.",
                Permissions = new List<Permission>
                {
                    new() { Icon = "📝", Label = "Post content",   Desc = "Create and schedule posts on your pages" },
                    new() { Icon = "📊", Label = "View analytics", Desc = "Read impressions, clicks, and followers" },
                    new() { Icon = "💬", Label = "Read comments",  Desc = "Monitor and respond to comments" }
                }
            },

            /* ── Facebook ──────────────────────────────────────────── */
            new PlatformConfig
            {
                Key         = "facebook",
                Name        = "Facebook",
                Color       = "#1877F2",
                AuthType    = "oauth",
                OAuthLabel  = "Continue with Facebook",
                Description = "Connect your Facebook Pages to publish posts, track engagement, and manage comments.",
                LogoSvg     = @"<svg viewBox='0 0 24 24' fill='#1877F2'><path d='M24 12.073c0-6.627-5.373-12-12-12s-12 5.373-12 12c0 5.99 4.388 10.954 10.125 11.854v-8.385H7.078v-3.47h3.047V9.43c0-3.007 1.792-4.669 4.533-4.669 1.312 0 2.686.235 2.686.235v2.953H15.83c-1.491 0-1.956.925-1.956 1.874v2.25h3.328l-.532 3.47h-2.796v8.385C19.612 23.027 24 18.062 24 12.073z'/></svg>",
                SuccessMsg  = "Your Facebook Page is now connected. New posts and comments will sync automatically.",
                Permissions = new List<Permission>
                {
                    new() { Icon = "📄", Label = "Manage Pages",      Desc = "Access and post to your Facebook Pages" },
                    new() { Icon = "📊", Label = "Insights access",   Desc = "Read page reach and engagement data" },
                    new() { Icon = "💬", Label = "Moderate content",  Desc = "Reply to and hide comments" },
                    new() { Icon = "📣", Label = "Run ads (optional)", Desc = "Boost posts directly from the panel" }
                }
            },

            /* ── X (Twitter) ───────────────────────────────────────── */
            new PlatformConfig
            {
                Key         = "twitter",
                Name        = "X (Twitter)",
                Color       = "#000000",
                AuthType    = "apikey",
                Description = "Connect your X account to schedule tweets, track performance, and monitor mentions.",
                LogoSvg     = @"<svg viewBox='0 0 24 24' fill='#000'><path d='M18.244 2.25h3.308l-7.227 8.26 8.502 11.24H16.17l-4.714-6.231-5.401 6.231H2.744l7.73-8.835L1.254 2.25H8.08l4.253 5.622zm-1.161 17.52h1.833L7.084 4.126H5.117z'/></svg>",
                SuccessMsg  = "Your X account has been linked. Tweets will now appear in your publishing calendar.",
                Fields = new List<ApiField>
                {
                    new() { Id = "tw_api_key",    Label = "API Key",        Type = "text",     Placeholder = "Enter your X API Key",  Hint = "Found in X Developer Portal → Keys and Tokens" },
                    new() { Id = "tw_api_secret", Label = "API Secret",     Type = "password", Placeholder = "Enter your API Secret", Hint = "" },
                    new() { Id = "tw_handle",     Label = "Twitter Handle", Type = "text",     Placeholder = "@yourhandle",           Hint = "The account you want to manage" }
                },
                Permissions = new List<Permission>
                {
                    new() { Icon = "🐦", Label = "Post tweets",      Desc = "Publish and schedule tweets and threads" },
                    new() { Icon = "📊", Label = "Read analytics",   Desc = "Impressions, engagements, and reach" },
                    new() { Icon = "🔔", Label = "Monitor mentions", Desc = "Track @mentions and replies" }
                }
            },

            /* ── Instagram ─────────────────────────────────────────── */
            new PlatformConfig
            {
                Key         = "instagram",
                Name        = "Instagram",
                Color       = "#E1306C",
                AuthType    = "oauth",
                OAuthLabel  = "Connect via Facebook Login",
                Note        = "⚠️ Instagram Business accounts must be linked to a Facebook Page.",
                Description = "Connect your Instagram Business account to schedule posts, Reels, and Stories.",
                LogoSvg     = @"<svg viewBox='0 0 24 24' fill='none'><defs><linearGradient id='igPR' x1='0' y1='24' x2='24' y2='0' gradientUnits='userSpaceOnUse'><stop stop-color='#f09433'/><stop offset='.25' stop-color='#e6683c'/><stop offset='.5' stop-color='#dc2743'/><stop offset='.75' stop-color='#cc2366'/><stop offset='1' stop-color='#bc1888'/></linearGradient></defs><rect x='2' y='2' width='20' height='20' rx='5' fill='url(#igPR)'/><circle cx='12' cy='12' r='4.5' stroke='#fff' stroke-width='1.8'/><circle cx='17' cy='7' r='1.2' fill='#fff'/></svg>",
                SuccessMsg  = "Your Instagram Business account is connected. Schedule your first post now!",
                Permissions = new List<Permission>
                {
                    new() { Icon = "📸", Label = "Publish media",   Desc = "Schedule feed posts, Reels, and Stories" },
                    new() { Icon = "📊", Label = "View insights",   Desc = "Reach, profile visits, and follower growth" },
                    new() { Icon = "💬", Label = "Manage comments", Desc = "Read and reply to post comments" }
                }
            },

            /* ── WhatsApp ──────────────────────────────────────────── */
            new PlatformConfig
            {
                Key         = "whatsapp",
                Name        = "WhatsApp",
                Color       = "#25D366",
                AuthType    = "apikey",
                Description = "Connect your WhatsApp Business account to manage customer conversations.",
                LogoSvg     = @"<svg viewBox='0 0 24 24' fill='#25D366'><path d='M17.472 14.382c-.297-.149-1.758-.867-2.03-.967-.273-.099-.471-.148-.67.15-.197.297-.767.966-.94 1.164-.173.199-.347.223-.644.075-.297-.15-1.255-.463-2.39-1.475-.883-.788-1.48-1.761-1.653-2.059-.173-.297-.018-.458.13-.606.134-.133.298-.347.446-.52.149-.174.198-.298.298-.497.099-.198.05-.371-.025-.52-.075-.149-.669-1.612-.916-2.207-.242-.579-.487-.5-.669-.51-.173-.008-.371-.01-.57-.01-.198 0-.52.074-.792.372-.272.297-1.04 1.016-1.04 2.479 0 1.462 1.065 2.875 1.213 3.074.149.198 2.096 3.2 5.077 4.487.709.306 1.262.489 1.694.625.712.227 1.36.195 1.871.118.571-.085 1.758-.719 2.006-1.413.248-.694.248-1.289.173-1.413-.074-.124-.272-.198-.57-.347m-5.421 7.403h-.004a9.87 9.87 0 0 1-5.031-1.378l-.361-.214-3.741.982.998-3.648-.235-.374a9.86 9.86 0 0 1-1.51-5.26c.001-5.45 4.436-9.884 9.888-9.884 2.64 0 5.122 1.03 6.988 2.898a9.825 9.825 0 0 1 2.893 6.994c-.003 5.45-4.437 9.884-9.885 9.884m8.413-18.297A11.815 11.815 0 0 0 12.05 0C5.495 0 .16 5.335.157 11.892c0 2.096.547 4.142 1.588 5.945L.057 24l6.305-1.654a11.882 11.882 0 0 0 5.683 1.448h.005c6.554 0 11.89-5.335 11.893-11.893a11.821 11.821 0 0 0-3.48-8.413z'/></svg>",
                SuccessMsg  = "WhatsApp Business is connected. Incoming messages will appear in Smart Inbox.",
                Fields = new List<ApiField>
                {
                    new() { Id = "wa_phone_id",    Label = "Phone Number ID",     Type = "text",     Placeholder = "e.g. 1234567890123", Hint = "From Meta Business Suite → WhatsApp → API Setup" },
                    new() { Id = "wa_token",       Label = "Access Token",        Type = "password", Placeholder = "Paste your token",   Hint = "Permanent token from Meta Business Suite" },
                    new() { Id = "wa_biz_account", Label = "Business Account ID", Type = "text",     Placeholder = "e.g. 9876543210",    Hint = "Your WhatsApp Business Account ID" }
                },
                Permissions = new List<Permission>
                {
                    new() { Icon = "💬", Label = "Send messages",    Desc = "Reply to customer messages via API" },
                    new() { Icon = "📋", Label = "Manage templates", Desc = "Create and send message templates" },
                    new() { Icon = "📊", Label = "View reports",     Desc = "Delivery rates and conversation stats" }
                }
            },

            /* ── Gmail ─────────────────────────────────────────────── */
            new PlatformConfig
            {
                Key         = "gmail",
                Name        = "Gmail",
                Color       = "#EA4335",
                AuthType    = "oauth",
                OAuthLabel  = "Sign in with Google",
                Description = "Connect your Gmail or Google Workspace to sync newsletters and email campaigns.",
                LogoSvg     = @"<svg viewBox='0 0 24 24' fill='none'><path d='M24 5.457v13.909c0 .904-.732 1.636-1.636 1.636h-3.819V11.73L12 16.64l-6.545-4.91v9.273H1.636A1.636 1.636 0 0 1 0 19.366V5.457c0-2.023 2.309-3.178 3.927-1.964L5.455 4.64 12 9.548l6.545-4.91 1.528-1.145C21.69 2.28 24 3.434 24 5.457z' fill='#EA4335'/><path d='M24 5.457v13.909c0 .904-.732 1.636-1.636 1.636h-3.819V11.73L12 16.64V9.548l6.545-4.91 1.528-1.145C21.69 2.28 24 3.434 24 5.457z' fill='#4285F4'/></svg>",
                SuccessMsg  = "Gmail is connected. Email campaign stats will now appear in your Reports.",
                Permissions = new List<Permission>
                {
                    new() { Icon = "📧", Label = "Read emails",    Desc = "Import newsletter subscriber data" },
                    new() { Icon = "📤", Label = "Send emails",    Desc = "Trigger email campaigns from the panel" },
                    new() { Icon = "🏷️", Label = "Manage labels", Desc = "Auto-label campaign-related threads" }
                }
            },

            /* ── YouTube ───────────────────────────────────────────── */
            new PlatformConfig
            {
                Key         = "youtube",
                Name        = "YouTube",
                Color       = "#FF0000",
                AuthType    = "oauth",
                OAuthLabel  = "Sign in with Google",
                Description = "Connect your YouTube channel to track video performance and schedule community posts.",
                LogoSvg     = @"<svg viewBox='0 0 24 24' fill='#FF0000'><path d='M23.498 6.186a3.016 3.016 0 0 0-2.122-2.136C19.505 3.545 12 3.545 12 3.545s-7.505 0-9.377.505A3.017 3.017 0 0 0 .502 6.186C0 8.07 0 12 0 12s0 3.93.502 5.814a3.016 3.016 0 0 0 2.122 2.136c1.871.505 9.376.505 9.376.505s7.505 0 9.377-.505a3.015 3.015 0 0 0 2.122-2.136C24 15.93 24 12 24 12s0-3.93-.502-5.814zM9.545 15.568V8.432L15.818 12z'/></svg>",
                SuccessMsg  = "Your YouTube channel is connected. Video analytics will appear in Reports.",
                Permissions = new List<Permission>
                {
                    new() { Icon = "🎥", Label = "Read channel data", Desc = "Views, subscribers, and watch time" },
                    new() { Icon = "📝", Label = "Community posts",   Desc = "Create and schedule community tab posts" },
                    new() { Icon = "💬", Label = "Comments",          Desc = "Monitor and reply to video comments" }
                }
            },

            /* ── TikTok ────────────────────────────────────────────── */
            new PlatformConfig
            {
                Key         = "tiktok",
                Name        = "TikTok",
                Color       = "#010101",
                AuthType    = "apikey",
                Description = "Connect your TikTok Business account to publish videos and track performance metrics.",
                LogoSvg     = @"<svg viewBox='0 0 24 24' fill='#000'><path d='M12.525.02c1.31-.02 2.61-.01 3.91-.02.08 1.53.63 3.09 1.75 4.17 1.12 1.11 2.7 1.62 4.24 1.79v4.03c-1.44-.05-2.89-.35-4.2-.97-.57-.26-1.1-.59-1.62-.93-.01 2.92.01 5.84-.02 8.75-.08 1.4-.54 2.79-1.35 3.94-1.31 1.92-3.58 3.17-5.91 3.21-1.43.08-2.86-.31-4.08-1.03-2.02-1.19-3.44-3.37-3.65-5.71-.02-.5-.03-1-.01-1.49.18-1.9 1.12-3.72 2.58-4.96 1.66-1.44 3.98-2.13 6.15-1.72.02 1.48-.04 2.96-.04 4.44-.99-.32-2.15-.23-3.02.37-.63.41-1.11 1.04-1.36 1.75-.21.51-.15 1.07-.14 1.61.24 1.64 1.82 3.02 3.5 2.87 1.12-.01 2.19-.66 2.77-1.61.19-.33.4-.67.41-1.06.1-1.79.06-3.57.07-5.36.01-4.03-.01-8.05.02-12.07z'/></svg>",
                SuccessMsg  = "TikTok Business is connected. Schedule your first video from Publishing.",
                Fields = new List<ApiField>
                {
                    new() { Id = "tt_client_key",    Label = "Client Key",    Type = "text",     Placeholder = "TikTok app client key", Hint = "From TikTok for Developers → App Management" },
                    new() { Id = "tt_client_secret", Label = "Client Secret", Type = "password", Placeholder = "Client secret value",   Hint = "" }
                },
                Permissions = new List<Permission>
                {
                    new() { Icon = "🎵", Label = "Upload videos",   Desc = "Schedule and publish TikTok videos" },
                    new() { Icon = "📊", Label = "View analytics",  Desc = "Views, likes, shares, and follower data" },
                    new() { Icon = "💬", Label = "Manage comments", Desc = "Read and reply to video comments" }
                }
            }
        };

        /// <summary>Look up a platform by key (case-insensitive). Returns null if not found.</summary>
        public static PlatformConfig? Get(string key) =>
            All.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }
}
