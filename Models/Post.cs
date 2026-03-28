//using System;
//using System.Collections.Generic;

//namespace SocialMediaApp.Models
//{
//    public class Post
//    {
//        public int Id { get; set; }
//        public string Title { get; set; }
//        public string Content { get; set; }
//        public string ImageUrl { get; set; }
//        public DateTime PublishedOn { get; set; }
//        public string PublishedBy { get; set; }
//        public string Platform { get; set; } // facebook, twitter, linkedin, instagram
//        public string Status { get; set; }   // published, scheduled, draft
//        public PostStats Stats { get; set; }
//    }

//    public class PostStats
//    {
//        public int Likes { get; set; }
//        public int Love { get; set; }
//        public int Wow { get; set; }
//        public int Haha { get; set; }
//        public int Angry { get; set; }
//        public int Sorry { get; set; }
//        public int TotalReactions { get; set; }
//        public int Comments { get; set; }
//        public int Shares { get; set; }
//        public int Clicks { get; set; }
//        public int TotalEngagement { get; set; }
//        public double EngagementRate { get; set; }
//        public int UniqueImpressions { get; set; }
//        public int UniqueFansImpressions { get; set; }
//        public int PostViralUniqueImpressions { get; set; }
//    }

//    public class PostListViewModel
//    {
//        public List<Post> Posts { get; set; } = new();
//        public string ActivePlatform { get; set; } = "facebook";
//        public string ActiveTab { get; set; } = "published"; // published, scheduled, drafts
//    }
//}


using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SocialMediaPanel.Models
{
    public class Post
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        public string Content { get; set; } = string.Empty;

        public string? ImageUrl { get; set; }

        // facebook | twitter | linkedin | instagram
        public string Platform { get; set; } = "facebook";

        // published | scheduled | draft | unpublished
        public string Status { get; set; } = "published";

        public string? PublishedBy { get; set; }

        public DateTime PublishedOn { get; set; } = DateTime.UtcNow;

        public DateTime? ScheduledAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Reactions
        public int StatLikes { get; set; } = 0;
        public int StatLove { get; set; } = 0;
        public int StatWow { get; set; } = 0;
        public int StatHaha { get; set; } = 0;
        public int StatAngry { get; set; } = 0;
        public int StatSorry { get; set; } = 0;

        // Engagements
        public int StatComments { get; set; } = 0;
        public int StatShares { get; set; } = 0;
        public int StatClicks { get; set; } = 0;

        // Impressions
        public int StatUniqueImpressions { get; set; } = 0;
        public int StatFanImpressions { get; set; } = 0;
        public int StatViralImpressions { get; set; } = 0;

        [ForeignKey("UserId")]
        public virtual User? User { get; set; }
    }
}