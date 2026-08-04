using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Controllers;
using SocialMediaPanel.Data;
using System.Text.Json;

namespace SocialMediaPanel.Services
{
    /// <summary>
    /// Resolves which Facebook Page / Instagram account a request should act on,
    /// for users who have connected more than one. Falls back to the first
    /// connected page/account when nothing has been explicitly selected, so
    /// single-page users see no behaviour change.
    ///
    /// No DB schema changes required: InstagramAccount rows aren't linked to a
    /// FacebookPageEntity by a foreign key (the live schema has no migrations,
    /// so we avoid adding columns). Instead, the linked page's access token is
    /// resolved on demand via the Graph API's instagram_business_account field
    /// and cached in session for the rest of that session.
    /// </summary>
    public class ActivePageService
    {
        private readonly AppDbContext _db;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IHttpClientFactory _httpClientFactory;

        public ActivePageService(AppDbContext db, IHttpContextAccessor httpContextAccessor, IHttpClientFactory httpClientFactory)
        {
            _db = db;
            _httpContextAccessor = httpContextAccessor;
            _httpClientFactory = httpClientFactory;
        }

        private ISession? Session => _httpContextAccessor.HttpContext?.Session;

        // ── Facebook ─────────────────────────────────────────────────────
        public async Task<List<FacebookPageEntity>> GetAllFacebookPagesAsync(int userId) =>
            await _db.FacebookPages
                .Where(p => p.user_id == userId.ToString())
                .OrderBy(p => p.id)
                .ToListAsync();

        public async Task<FacebookPageEntity?> GetActiveFacebookPageAsync(int userId, string? requestedPageId = null)
        {
            var pages = await GetAllFacebookPagesAsync(userId);
            if (pages.Count == 0) return null;

            if (!string.IsNullOrEmpty(requestedPageId))
            {
                var requested = pages.FirstOrDefault(p => p.page_id == requestedPageId);
                if (requested != null)
                {
                    Session?.SetString("ActiveFbPageId", requested.page_id);
                    return requested;
                }
            }

            var activeId = Session?.GetString("ActiveFbPageId");
            if (!string.IsNullOrEmpty(activeId))
            {
                var active = pages.FirstOrDefault(p => p.page_id == activeId);
                if (active != null) return active;
            }

            return pages.First();
        }

        // ── Instagram ────────────────────────────────────────────────────
        public async Task<List<InstagramAccount>> GetAllInstagramAccountsAsync(int userId) =>
            await _db.InstagramAccounts
                .Where(a => a.UserId == userId.ToString())
                .OrderBy(a => a.Id)
                .ToListAsync();

        public async Task<InstagramAccount?> GetActiveInstagramAccountAsync(int userId, string? requestedIgId = null)
        {
            var accounts = await GetAllInstagramAccountsAsync(userId);
            if (accounts.Count == 0) return null;

            if (!string.IsNullOrEmpty(requestedIgId))
            {
                var requested = accounts.FirstOrDefault(a => a.InstagramUserId == requestedIgId);
                if (requested != null)
                {
                    Session?.SetString("ActiveIgAccountId", requested.InstagramUserId);
                    return requested;
                }
            }

            var activeId = Session?.GetString("ActiveIgAccountId");
            if (!string.IsNullOrEmpty(activeId))
            {
                var active = accounts.FirstOrDefault(a => a.InstagramUserId == activeId);
                if (active != null) return active;
            }

            return accounts.First();
        }

        /// <summary>
        /// Finds the Facebook Page (and its access token) that a given Instagram
        /// Business Account is linked to, by checking each of the user's pages'
        /// instagram_business_account field. Result is cached in session per IG
        /// account id for the rest of the session to avoid repeat API calls.
        /// </summary>
        public async Task<FacebookPageEntity?> GetLinkedPageForInstagramAsync(int userId, string instagramUserId)
        {
            var cacheKey = $"IgLinkedPage:{instagramUserId}";
            var cachedPageId = Session?.GetString(cacheKey);
            var pages = await GetAllFacebookPagesAsync(userId);
            if (pages.Count == 0) return null;

            if (!string.IsNullOrEmpty(cachedPageId))
            {
                var cached = pages.FirstOrDefault(p => p.page_id == cachedPageId);
                if (cached != null) return cached;
            }

            if (pages.Count == 1) return pages[0];

            var client = _httpClientFactory.CreateClient();
            foreach (var page in pages)
            {
                try
                {
                    var url = $"https://graph.facebook.com/v19.0/{page.page_id}" +
                              $"?fields=instagram_business_account&access_token={page.page_access_token}";
                    var body = await client.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("instagram_business_account", out var ig) &&
                        ig.TryGetProperty("id", out var idProp) &&
                        idProp.GetString() == instagramUserId)
                    {
                        Session?.SetString(cacheKey, page.page_id);
                        return page;
                    }
                }
                catch { /* try next page */ }
            }

            return null;
        }
    }
}
