namespace SocialMediaPanel.Models
{
    public class PageReply
    {
        public int Id { get; set; }
        public int SourceId { get; set; }          // FK → PageComment.Id or PageMessage.Id
        public string SourceType { get; set; } = "comment";  // "comment" | "message"
        public string Platform { get; set; } = "";
        public string PageId { get; set; } = "";
        public string PostId { get; set; } = "";
        public string ReplyText { get; set; } = "";
        public string SentByPageId { get; set; } = ""; // your page/account ID
        public bool IsSuccess { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}