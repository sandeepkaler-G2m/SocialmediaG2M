using System;
using System.Collections.Generic;

namespace SocialMediaPanel.ViewModels
{
    public class PostListViewModel
    {
        public List<PostRowViewModel> Posts { get; set; } = new();
        public string ActivePlatform { get; set; } = "facebook";
        public string ActiveTab { get; set; } = "published";
    }

    public class PostRowViewModel
    {
        public string PostId { get; set; } = string.Empty;
        public string? PageId { get; set; }
        public string Platform { get; set; } = "facebook";
        public string? Message { get; set; }
        public string? FullPicture { get; set; }
        public DateTime? CreatedTime { get; set; }
        public string? PermalinkUrl { get; set; }
        public int LikesCount { get; set; }
        public int CommentsCount { get; set; }
        public int SharesCount { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int TotalEngagement => LikesCount + CommentsCount + SharesCount;
    }

    public class PostDetailViewModel
    {
        public string PostId { get; set; } = string.Empty;
        public string? PageId { get; set; }
        public string Platform { get; set; } = "";
        public string? Message { get; set; }
        public string? FullPicture { get; set; }
        public DateTime? CreatedTime { get; set; }
        public string? PermalinkUrl { get; set; }
        public int LikesCount { get; set; }
        public int CommentsCount { get; set; }
        public int SharesCount { get; set; }
        public int Reach { get; set; }
        public int Impressions { get; set; }
        public int SavesCount { get; set; }
        public DateTime UpdatedAt { get; set; }

        public string? MediaType { get; set; }
        public List<CommentRowViewModel> Comments { get; set; } = new();

        public List<InstagramChildMedia>? CarouselChildren { get; set; }

        public int TotalEngagement => LikesCount + CommentsCount + SharesCount;
        public double EngagementRate =>
            Impressions > 0
                ? Math.Round((double)TotalEngagement / Impressions * 100, 1)
                : 0;
    }
    public class InstagramChildMedia
    {
        public string Id { get; set; } = "";
        public string? MediaUrl { get; set; }
        public string? MediaType { get; set; }  // IMAGE or VIDEO
    }
    public class CommentRowViewModel
    {
        public string? CommentId { get; set; }  // Facebook comment ID (reply ke liye)
        public string SenderName { get; set; } = string.Empty;
        public string? Message { get; set; }
        public DateTime? CommentTime { get; set; }
        public int LikesCount { get; set; }  // Comment likes

        // Nested replies
        public List<CommentRowViewModel> Replies { get; set; } = new();
    }
}