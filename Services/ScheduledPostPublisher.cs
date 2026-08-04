using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Controllers;
using SocialMediaPanel.Data;

namespace SocialMediaPanel.Services
{
    /// <summary>
    /// Polls SocialPosts every minute for rows with status="scheduled" whose
    /// scheduled_at has passed, and publishes them via the same
    /// PostPublishingService the immediate-publish path uses. Runs for the
    /// lifetime of the app (registered as a hosted service in Program.cs).
    /// </summary>
    public class ScheduledPostPublisher : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ScheduledPostPublisher> _logger;
        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

        public ScheduledPostPublisher(IServiceScopeFactory scopeFactory, ILogger<ScheduledPostPublisher> logger)
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
                    await PublishDuePostsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError("ScheduledPostPublisher tick failed: {Msg}", ex.Message);
                }

                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (TaskCanceledException) { /* shutting down */ }
            }
        }

        private async Task PublishDuePostsAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var publishing = scope.ServiceProvider.GetRequiredService<PostPublishingService>();
            var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

            var due = await db.SocialPosts
                .Where(p => p.status == "scheduled" && p.scheduled_at != null && p.scheduled_at <= DateTime.UtcNow)
                .ToListAsync(ct);

            if (due.Count == 0) return;

            _logger.LogInformation("ScheduledPostPublisher: {Count} post(s) due", due.Count);

            var baseUrl = config["AppBaseUrl"]?.TrimEnd('/') ?? "";

            foreach (var post in due)
            {
                try
                {
                    var imageDataList = new List<(byte[] bytes, string contentType, string fileName)>();
                    var savedFullUrls = new List<string>();

                    if (!string.IsNullOrEmpty(post.media_url))
                    {
                        foreach (var relativePath in post.media_url.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var fullPath = Path.Combine(env.WebRootPath, relativePath.TrimStart('/').Replace("uploads/", "uploads" + Path.DirectorySeparatorChar));
                            if (!File.Exists(fullPath))
                            {
                                _logger.LogWarning("Scheduled post {Id}: media file missing at {Path}", post.id, fullPath);
                                continue;
                            }

                            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
                            var ext = Path.GetExtension(fullPath).ToLower();
                            var contentType = ext switch
                            {
                                ".png" => "image/png",
                                ".gif" => "image/gif",
                                ".webp" => "image/webp",
                                _ => "image/jpeg"
                            };
                            imageDataList.Add((bytes, contentType, Path.GetFileName(fullPath)));
                            savedFullUrls.Add($"{baseUrl}{relativePath}");
                        }
                    }

                    var isFacebook = post.platform?.ToLower() == "facebook";
                    var result = isFacebook
                        ? await publishing.PublishToFacebookAsync(post.user_id, post.page_id, post.message, imageDataList)
                        : await publishing.PublishToInstagramAsync(post.user_id, post.page_id, post.message, baseUrl, imageDataList, savedFullUrls);

                    if (result.Success)
                    {
                        post.status = "published";
                        post.post_id = result.PostId ?? "";
                        post.page_id = result.ResolvedPageId ?? post.page_id;
                        _logger.LogInformation("Scheduled post {Id} published — postId={PostId}", post.id, result.PostId);
                    }
                    else
                    {
                        post.status = "failed";
                        _logger.LogWarning("Scheduled post {Id} failed: {Msg}", post.id, result.Message);
                    }
                }
                catch (Exception ex)
                {
                    post.status = "failed";
                    _logger.LogError("Scheduled post {Id} threw: {Msg}", post.id, ex.Message);
                }
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
