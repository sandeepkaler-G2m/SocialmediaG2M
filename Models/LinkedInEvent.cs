using System.ComponentModel.DataAnnotations;

namespace SocialMediaPanel.Models
{
    // A LinkedIn Event created via the Events Management API (r_events /
    // rw_events). LinkedIn's own id/URNs are stored once the event has
    // actually been created + posted (an event isn't fetchable/visible
    // until it's been posted — see LinkedInService.PostEventAsync).
    public class LinkedInEvent
    {
        [Key]
        public int Id { get; set; }

        public string UserId { get; set; } = "";
        public int LinkedInIntegrationId { get; set; }

        [Required]
        public string Name { get; set; } = "";
        public string? Description { get; set; }

        // online_livevideo | online_external | inperson
        public string EventType { get; set; } = "";

        public string OrganizerUrn { get; set; } = "";
        public long StartsAt { get; set; } // epoch millis
        public long? EndsAt { get; set; }

        public string? ExternalUrl { get; set; }     // online_external / inperson
        public string? AddressJson { get; set; }      // inperson

        // Populated once created via POST /rest/events.
        public string? LinkedInEventId { get; set; }
        public string? LiveVideoUrn { get; set; }
        public string? VanityName { get; set; }

        // Populated once posted via POST /rest/posts (makes the event visible).
        public string? UgcPostUrn { get; set; }

        // draft | posted | failed
        public string Status { get; set; } = "draft";
        public string? ErrorMessage { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
