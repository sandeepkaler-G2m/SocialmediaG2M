using System.ComponentModel.DataAnnotations;

namespace SocialMediaPanel.Models
{
    // Bulk WhatsApp broadcast — one campaign fans out to many recipients via
    // G2M's WhatsApp send API (see WhatsAppBusinessService/WhatsAppCampaignController).
    // WhatsApp policy requires an approved template for any first-contact/
    // outside-24h-window send, so MessageType is usually "template", but
    // text/media are allowed too for lists known to be within-window.
    public class WhatsAppCampaign
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        [Required]
        public string Name { get; set; } = "";

        // template | text | image | video | document | audio
        public string MessageType { get; set; } = "template";

        public string? TemplateName { get; set; }
        public string? LanguageCode { get; set; }
        // Comma-separated body params — same for every recipient in v1 (no per-row personalization).
        public string? TemplateParams { get; set; }

        public string? MessageText { get; set; }
        public string? MediaUrl { get; set; }
        public string? Caption { get; set; }
        public string? FileName { get; set; }

        // pending | scheduled | running | completed
        public string Status { get; set; } = "pending";

        public int TotalRecipients { get; set; }
        public int SentCount { get; set; }
        public int FailedCount { get; set; }

        // Which connected number this campaign sends from — captured at
        // creation time so the list can show it even after numbers change.
        public string? PhoneNumberId { get; set; }

        // MARKETING | UTILITY | AUTHENTICATION — copied from the chosen
        // WhatsAppTemplate at creation time, purely for display in the list.
        public string? TemplateType { get; set; }

        // Null = send immediately. Set = held until ScheduledWhatsAppCampaignPublisher
        // picks it up at/after this time (see that hosted service).
        public DateTime? ScheduledAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
    }
}
