namespace SocialMediaPanel.Models
{
    public class UserToken
    {
        public int Id { get; set; }

        public int userId { get; set; }

        public string? username { get; set; }

        public string? facebooktoken { get; set; }
        public string? instagramtoken { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
