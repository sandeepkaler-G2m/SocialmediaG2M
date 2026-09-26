using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Controllers;
using SocialMediaPanel.Data;

namespace SocialMediaPanel.Services
{
    /// <summary>
    /// Polls WhatsAppCampaigns every minute for rows with status="scheduled"
    /// whose ScheduledAt has passed, and runs them via
    /// WhatsAppCampaignController.RunCampaignAsync — the same send loop
    /// "Send Now" uses. Same pattern as ScheduledPostPublisher for the
    /// Posts feature.
    /// </summary>
    public class ScheduledWhatsAppCampaignPublisher : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ScheduledWhatsAppCampaignPublisher> _logger;
        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

        public ScheduledWhatsAppCampaignPublisher(IServiceScopeFactory scopeFactory, ILogger<ScheduledWhatsAppCampaignPublisher> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunDueCampaignsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError("ScheduledWhatsAppCampaignPublisher tick failed: {Msg}", ex.Message);
                }

                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (TaskCanceledException) { /* shutting down */ }
            }
        }

        private async Task RunDueCampaignsAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var due = await db.WhatsAppCampaigns
                .Where(c => c.Status == "scheduled" && c.ScheduledAt != null && c.ScheduledAt <= DateTime.UtcNow)
                .ToListAsync(ct);

            if (due.Count == 0) return;

            _logger.LogInformation("ScheduledWhatsAppCampaignPublisher: {Count} campaign(s) due", due.Count);

            foreach (var campaign in due)
            {
                var integration = await db.WhatsAppIntegrations
                    .Where(w => w.UserId == campaign.UserId && w.IsActive && w.PhoneNumberId == campaign.PhoneNumberId)
                    .FirstOrDefaultAsync(ct)
                    ?? await db.WhatsAppIntegrations.Where(w => w.UserId == campaign.UserId && w.IsActive && w.IsDefault).FirstOrDefaultAsync(ct);

                if (integration == null)
                {
                    _logger.LogWarning("Scheduled campaign {Id}: no active WhatsApp integration found — marking failed", campaign.Id);
                    campaign.Status = "completed";
                    campaign.CompletedAt = DateTime.UtcNow;
                    continue;
                }

                // Fire-and-forget per campaign — RunCampaignAsync flips
                // status to "running" itself the moment it starts.
                var campaignId = campaign.Id;
                var apiKey = integration.AccessToken;
                var phoneNumberId = integration.PhoneNumberId;
                _ = Task.Run(() => WhatsAppCampaignController.RunCampaignAsync(campaignId, apiKey, phoneNumberId, _scopeFactory), ct);
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
