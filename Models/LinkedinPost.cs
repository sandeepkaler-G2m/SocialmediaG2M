using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

[Table("linkedin_posts")]
public class LinkedinPosts
{
    public int Id { get; set; }

    public string UserId { get; set; }

    // Which connected LinkedIn account this post was made through — nullable
    // because posts made before multi-account support won't have one.
    public int? LinkedInIntegrationId { get; set; }

    public string? PostId { get; set; }          // ✅ nullable
    public string PostText { get; set; }

    public string? ArticleUrl { get; set; }      // ✅ nullable
    public string? ImageUrl { get; set; }        // ✅ nullable

    public string Status { get; set; }

    public string? ErrorMessage { get; set; }    // ✅ nullable

    public DateTime? PostedAt { get; set; }      // ✅ nullable

    public DateTime CreatedAt { get; set; }
}