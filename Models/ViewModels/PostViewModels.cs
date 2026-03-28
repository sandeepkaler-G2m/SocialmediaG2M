using System;
using System.Collections.Generic;

namespace SocialMediaPanel.ViewModels
{
    // ── Posts list page ───────────────────────────────────────────────
    public class PostListViewModel
    {
        public List<PostRowViewModel> Posts { get; set; } = new();
        public string ActivePlatform { get; set; } = "facebook";
        public string ActiveTab { get; set; } = "published";
    }

    // ── One row in posts table ────────────────────────────────────────
    public class PostRowViewModel
    {
        // From post_insights table
        public int Id { get; set; }
        public string PostId { get; set; } = string.Empty;
        public string? PageId { get; set; }
        public string Platform { get; set; } = "facebook";
        public int LikesCount { get; set; }
        public int CommentsCount { get; set; }
        public int SharesCount { get; set; }
        public int Reach { get; set; }
        public int Impressions { get; set; }
        public int SavesCount { get; set; }
        public DateTime UpdatedAt { get; set; }

        // From Facebook Graph API
        public string? Message { get; set; }
        public string? FullPicture { get; set; }
        public DateTime? CreatedTime { get; set; }
        public string? PermalinkUrl { get; set; }

        // Computed
        public int TotalEngagement => LikesCount + CommentsCount + SharesCount;
    }

    // ── Modal detail ──────────────────────────────────────────────────
    public class PostDetailViewModel
    {
        // From post_insights table
        public int Id { get; set; }
        public string PostId { get; set; } = string.Empty;
        public string? PageId { get; set; }
        public string Platform { get; set; } = "facebook";
        public int LikesCount { get; set; }
        public int CommentsCount { get; set; }
        public int SharesCount { get; set; }
        public int Reach { get; set; }
        public int Impressions { get; set; }
        public int SavesCount { get; set; }
        public DateTime UpdatedAt { get; set; }

        // From Facebook Graph API
        public string? Message { get; set; }
        public string? FullPicture { get; set; }
        public DateTime? CreatedTime { get; set; }
        public string? PermalinkUrl { get; set; }

        // Comments from page_comments table
        public List<CommentRowViewModel> Comments { get; set; } = new();

        // Computed
        public int TotalEngagement => LikesCount + CommentsCount + SharesCount;
        public double EngagementRate =>
            Impressions > 0
                ? Math.Round((double)TotalEngagement / Impressions * 100, 1)
                : 0;
    }

    // ── One comment ───────────────────────────────────────────────────
    public class CommentRowViewModel
    {
        public string SenderName { get; set; } = string.Empty;
        public string? Message { get; set; }
        public DateTime? CommentTime { get; set; }
    }
}