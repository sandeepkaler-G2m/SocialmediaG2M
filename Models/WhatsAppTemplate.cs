using System.ComponentModel.DataAnnotations;

namespace SocialMediaPanel.Models
{
    // A user-maintained record of a WhatsApp template name they've had
    // approved (via G2M/Meta) — there's no API to fetch real templates back
    // from G2M yet, so this is a self-service list the user keeps in sync
    // themselves, used to quickly pick a template when sending/campaigning.
    public class WhatsAppTemplate
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        [Required]
        public string Name { get; set; } = "";

        public string LanguageCode { get; set; } = "en";

        // MARKETING | UTILITY | AUTHENTICATION — matches Meta's own template
        // categories, shown on the campaign list once used.
        public string TemplateType { get; set; } = "UTILITY";

        // Comma-separated example values — purely a memory aid shown next to
        // the template name, not sent anywhere on its own.
        public string? SampleParams { get; set; }

        public string? Description { get; set; }

        // Full structured definition (header/body/footer/buttons) as JSON —
        // shaped to map directly onto Meta's real message_templates
        // "components" array (see WhatsAppTemplateController for the exact
        // schema), so wiring real Meta/G2M submission later is a transform,
        // not a rewrite.
        public string? DefinitionJson { get; set; }

        // pending | approved | rejected — Meta's own review can take time,
        // so a template isn't usable in a campaign until UsableAt passes
        // (see Create Campaign's template picker).
        public string Status { get; set; } = "pending";

        public DateTime? UsableAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
