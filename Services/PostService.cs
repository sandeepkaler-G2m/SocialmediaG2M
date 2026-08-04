using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SocialMediaPanel.Controllers;
using SocialMediaPanel.Data;
using SocialMediaPanel.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SocialMediaPanel.Services
{
    public interface IPostService
    {
        Task<string?> GetTokenAsync(string userEmail);
        Task<string?> GetInstagramTokenAsync(string userEmail);
        Task<PostListViewModel> GetPostsAsync(int userId, string userEmail, string? pageId, string platform = "facebook");
        Task<PostListViewModel> GetInstagramPostsAsync(int userId, string userEmail, string? pageId);
        Task<PostDetailViewModel?> GetPostDetailAsync(string postId, int userId, string userEmail, string? pageId, string platform);
        Task<bool> LikePostAsync(string postId, int userId, string userEmail, string? pageId);
        Task<bool> AddCommentAsync(string postId, string message, int userId, string userEmail, string? pageId, string platform = "facebook");
        Task<bool> ReplyToCommentAsync(string commentId, string message, int userId, string userEmail, string? pageId, string platform = "facebook");
        Task<bool> DeleteCommentAsync(string commentId, int userId, string? pageId, string platform = "facebook");
        Task<bool> HideCommentAsync(string commentId, bool hide, int userId, string? pageId, string platform = "facebook");
        //  Task<PostListViewModel> GetDraftPostsAsync(int userId, string platform);
        Task<PostListViewModel> GetDraftPostsAsync(int userId, string platform);
        Task<PostDetailViewModel?> GetDraftByIdAsync(string draftId, int userId);
    }

    public class PostService : IPostService
    {
        private readonly AppDbContext _db;
        private readonly IHttpClientFactory _http;
        private readonly ILogger<PostService> _logger;
        private readonly ActivePageService _activePages;
        private readonly InsightsSyncService _insightsSync;

        public PostService(AppDbContext db, IHttpClientFactory http, ILogger<PostService> logger, ActivePageService activePages, InsightsSyncService insightsSync)
        {
            _db = db;
            _http = http;
            _logger = logger;
            _activePages = activePages;
            _insightsSync = insightsSync;
        }

        // ── Resolve which Facebook Page a request should act on ─────────
        // Prefers the DB-backed FacebookPages table (multi-page aware, set via
        // the page switcher / ActivePageService). Falls back to the legacy
        // live "/me/accounts" lookup for older accounts that never got a
        // FacebookPages row written.
        private async Task<(string pageId, string pageToken, string pageName)?> ResolveFacebookPageAsync(
            int userId, string? requestedPageId, string fallbackUserEmail)
        {
            var page = await _activePages.GetActiveFacebookPageAsync(userId, requestedPageId);
            if (page != null) return (page.page_id, page.page_access_token, page.page_name);

            var userToken = await GetTokenAsync(fallbackUserEmail);
            if (string.IsNullOrEmpty(userToken)) return null;
            var legacy = await GetFirstPageAsync(userToken);
            return legacy == null ? null : (legacy.Value.pageId, legacy.Value.pageToken, "");
        }

        // ── Resolve which Instagram account a request should act on ─────
        // Instagram write/read operations all go through the linked Facebook
        // Page's access token (Instagram via Facebook Login), never a
        // "native" graph.instagram.com token — that flow isn't implemented.
        private async Task<(string igUserId, string pageToken, string igUsername)?> ResolveInstagramAccountAsync(
            int userId, string? requestedIgId)
        {
            var ig = await _activePages.GetActiveInstagramAccountAsync(userId, requestedIgId);
            if (ig == null) return null;

            var linkedPage = await _activePages.GetLinkedPageForInstagramAsync(userId, ig.InstagramUserId);
            if (linkedPage == null) return null;

            return (ig.InstagramUserId, linkedPage.page_access_token, ig.Username);
        }

        // ── Facebook token ────────────────────────────────────────────
        public async Task<string?> GetTokenAsync(string userEmail)
        {
            var row = await _db.UserTokens
                .Where(t => t.username == userEmail
                         && t.facebooktoken != null
                         && t.facebooktoken != "")
                .OrderByDescending(t => t.CreatedAt)
                .FirstOrDefaultAsync();
            if (row == null) { _logger.LogWarning("No FB token: {e}", userEmail); return null; }
            return row.facebooktoken;
        }

        // ── Instagram token ───────────────────────────────────────────
        public async Task<string?> GetInstagramTokenAsync(string userEmail)
        {
            var row = await _db.UserTokens
                .Where(t => t.username == userEmail
                         && t.instagramtoken != null
                         && t.instagramtoken != "")
                .OrderByDescending(t => t.CreatedAt)
                .FirstOrDefaultAsync();
            return row?.instagramtoken;
        }

        // ── Facebook page ID + page token ─────────────────────────────
        public async Task<(string pageId, string pageToken)?> GetFirstPageAsync(string userToken)
        {
            try
            {
                var client = _http.CreateClient();
                var resp = await client.GetAsync(
                    $"https://graph.facebook.com/v19.0/me/accounts"
                    + $"?fields=id,name,access_token&access_token={userToken}");

                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

                var pages = data.EnumerateArray().ToList();
                if (!pages.Any()) return null;

                var first = pages[0];
                var pageId = first.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                var pageToken = first.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";

                if (string.IsNullOrEmpty(pageId) || string.IsNullOrEmpty(pageToken)) return null;
                return (pageId, pageToken);
            }
            catch (Exception ex) { _logger.LogError("GetPage ex: {m}", ex.Message); return null; }
        }

        // ════════════════════════════════════════════════════════════
        // 1. FACEBOOK POSTS LIST
        // ════════════════════════════════════════════════════════════
        public async Task<PostListViewModel> GetPostsAsync(int userId, string userEmail, string? pageId, string platform = "facebook")
        {
            var resolved = await ResolveFacebookPageAsync(userId, pageId, userEmail);
            if (resolved == null)
                return new PostListViewModel { ActivePlatform = platform };

            var (resolvedPageId, pageToken, _) = resolved.Value;
            pageId = resolvedPageId;

            try
            {
                var client = _http.CreateClient();
                var url = $"https://graph.facebook.com/v19.0/{pageId}/posts"
                           + $"?fields=id,message,full_picture,created_time,permalink_url"
                           + $",likes.summary(true),comments.summary(true),shares"
                           + $"&limit=25&access_token={pageToken}";

                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return new PostListViewModel { ActivePlatform = platform };

                using var doc = JsonDocument.Parse(body);
                var rows = new List<PostRowViewModel>();

                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var post in data.EnumerateArray())
                    {
                        var row = new PostRowViewModel
                        {
                            PostId = post.TryGetProperty("id", out var pid) ? pid.GetString() ?? "" : "",
                            PageId = pageId,
                            Platform = platform,
                        };
                        if (post.TryGetProperty("message", out var msg)) row.Message = msg.GetString();
                        if (post.TryGetProperty("full_picture", out var pic)) row.FullPicture = pic.GetString();
                        if (post.TryGetProperty("permalink_url", out var pl)) row.PermalinkUrl = pl.GetString();
                        if (post.TryGetProperty("created_time", out var ct) &&
                            DateTime.TryParse(ct.GetString(), out var dt)) row.CreatedTime = dt;
                        if (post.TryGetProperty("likes", out var likes) &&
                            likes.TryGetProperty("summary", out var ls) &&
                            ls.TryGetProperty("total_count", out var lc))
                            row.LikesCount = lc.GetInt32();
                        if (post.TryGetProperty("comments", out var cmts) &&
                            cmts.TryGetProperty("summary", out var cs) &&
                            cs.TryGetProperty("total_count", out var cc))
                            row.CommentsCount = cc.GetInt32();
                        if (post.TryGetProperty("shares", out var sh) &&
                            sh.TryGetProperty("count", out var sc))
                            row.SharesCount = sc.GetInt32();

                        row.UpdatedAt = row.CreatedTime?.ToUniversalTime() ?? DateTime.UtcNow;
                        rows.Add(row);
                    }
                }
                return new PostListViewModel { Posts = rows, ActivePlatform = platform, ActiveTab = "published" };
            }
            catch (Exception ex)
            {
                _logger.LogError("GetPosts ex: {m}", ex.Message);
                return new PostListViewModel { ActivePlatform = platform };
            }
        }


        public async Task<PostListViewModel> GetDraftPostsAsync(int userId, string platform)
        {
            // SocialPosts DB table se drafts uthao
            var drafts = await _db.SocialPosts
                .Where(p => p.user_id == userId && p.status == "draft")
                .OrderByDescending(p => p.created_at)
                .ToListAsync();

            var rows = drafts.Select(p => new PostRowViewModel
            {
                PostId = p.id.ToString(),   // draft ka id use karo
                Platform = p.platform ?? platform,
                Message = p.message,
                UpdatedAt = p.created_at,
                CreatedTime = p.created_at,

            }).ToList();

            return new PostListViewModel
            {
                Posts = rows,
                ActivePlatform = platform,
                ActiveTab = "draft"
            };
        }

        public async Task<PostDetailViewModel?> GetDraftByIdAsync(string draftId, int userId)
        {
            // id se match karo
            if (!int.TryParse(draftId, out var id)) return null;

            var draft = await _db.SocialPosts
                .Where(p => p.id == id
                         && p.user_id == userId
                         && p.status == "draft")
                .FirstOrDefaultAsync();

            if (draft == null) return null;

            return new PostDetailViewModel
            {
                PostId = draft.id.ToString(),
                Platform = draft.platform ?? "",
                Message = draft.message,
                media_url = draft.media_url, // 👈 Ye line add kar service mein!
                UpdatedAt = draft.created_at,
                DraftMediaUrls = !string.IsNullOrEmpty(draft.media_url)
                         ? draft.media_url.Split(',').ToList()
                         : new List<string>()
            };
        }

        // ════════════════════════════════════════════════════════════
        // 2. INSTAGRAM POSTS LIST
        // ════════════════════════════════════════════════════════════
        //public async Task<PostListViewModel> GetInstagramPostsAsync(string userEmail)
        //{
        //    var accessToken = await GetInstagramTokenAsync(userEmail);
        //    if (string.IsNullOrEmpty(accessToken))
        //        return new PostListViewModel { ActivePlatform = "instagram" };

        //    try
        //    {
        //        var client = _http.CreateClient();
        //        var url = "https://graph.instagram.com/me/media"
        //                   + "?fields=id,caption,media_url,permalink,timestamp"
        //                   + $"&access_token={accessToken}";

        //        var resp = await client.GetAsync(url);
        //        var body = await resp.Content.ReadAsStringAsync();
        //        if (!resp.IsSuccessStatusCode)
        //            return new PostListViewModel { ActivePlatform = "instagram" };

        //        using var doc = JsonDocument.Parse(body);
        //        var rows = new List<PostRowViewModel>();

        //        if (doc.RootElement.TryGetProperty("data", out var data))
        //        {
        //            foreach (var post in data.EnumerateArray())
        //            {
        //                var row = new PostRowViewModel { Platform = "instagram" };
        //                if (post.TryGetProperty("id", out var pid)) row.PostId = pid.GetString() ?? "";
        //                if (post.TryGetProperty("caption", out var cap)) row.Message = cap.GetString();
        //                if (post.TryGetProperty("media_url", out var mu)) row.FullPicture = mu.GetString();
        //                if (post.TryGetProperty("permalink", out var pl)) row.PermalinkUrl = pl.GetString();
        //                if (post.TryGetProperty("timestamp", out var ts) &&
        //                    DateTime.TryParse(ts.GetString(), out var dt)) row.CreatedTime = dt;
        //                row.UpdatedAt = row.CreatedTime?.ToUniversalTime() ?? DateTime.UtcNow;
        //                rows.Add(row);
        //            }
        //        }
        //        return new PostListViewModel { Posts = rows, ActivePlatform = "instagram", ActiveTab = "published" };
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError("GetIGPosts ex: {m}", ex.Message);
        //        return new PostListViewModel { ActivePlatform = "instagram" };
        //    }
        //}

        public async Task<PostListViewModel> GetInstagramPostsAsync(int userId, string userEmail, string? pageId)
        {
            var resolved = await ResolveInstagramAccountAsync(userId, pageId);
            if (resolved == null)
                return new PostListViewModel { ActivePlatform = "instagram" };

            var (igUserId, pageToken, _) = resolved.Value;

            try
            {
                var client = _http.CreateClient();

                // Instagram posts fetch karo — graph.facebook.com se
                var url = $"https://graph.facebook.com/v19.0/{igUserId}/media"
                        + $"?fields=id,caption,media_url,permalink,timestamp,like_count,comments_count"
                        + $"&access_token={pageToken}";

                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    return new PostListViewModel { ActivePlatform = "instagram" };

                using var doc = JsonDocument.Parse(body);
                var rows = new List<PostRowViewModel>();

                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var post in data.EnumerateArray())
                    {
                        var row = new PostRowViewModel { Platform = "instagram" };
                        if (post.TryGetProperty("id", out var pid)) row.PostId = pid.GetString() ?? "";
                        if (post.TryGetProperty("caption", out var cap)) row.Message = cap.GetString();
                        if (post.TryGetProperty("media_url", out var mu)) row.FullPicture = mu.GetString();
                        if (post.TryGetProperty("permalink", out var pl)) row.PermalinkUrl = pl.GetString();
                        if (post.TryGetProperty("like_count", out var lc)) row.LikesCount = lc.GetInt32();
                        if (post.TryGetProperty("comments_count", out var cc)) row.CommentsCount = cc.GetInt32();
                        if (post.TryGetProperty("timestamp", out var ts) &&
                            DateTime.TryParse(ts.GetString(), out var dt)) row.CreatedTime = dt;
                        row.UpdatedAt = row.CreatedTime?.ToUniversalTime() ?? DateTime.UtcNow;
                        rows.Add(row);
                    }
                }
                return new PostListViewModel { Posts = rows, ActivePlatform = "instagram", ActiveTab = "published" };
            }
            catch (Exception ex)
            {
                _logger.LogError("GetIGPosts ex: {m}", ex.Message);
                return new PostListViewModel { ActivePlatform = "instagram" };
            }
        }

        // ════════════════════════════════════════════════════════════
        // 3. POST DETAIL — routes to FB or IG
        // ════════════════════════════════════════════════════════════
        public async Task<PostDetailViewModel?> GetPostDetailAsync(string postId, int userId, string userEmail, string? pageId, string platform)
        {
            if (platform.ToLower() == "instagram")
            {
                var igResolved = await ResolveInstagramAccountAsync(userId, pageId);
                if (igResolved == null) return null;
                return await GetInstagramPostDetailAsync(postId, igResolved.Value.pageToken, igResolved.Value.igUserId);
            }

            // ── Facebook ──────────────────────────────────────────────
            var resolved = await ResolveFacebookPageAsync(userId, pageId, userEmail);
            if (resolved == null) return null;
            var (resolvedPageId, pageToken, _) = resolved.Value;
            pageId = resolvedPageId;

            try
            {
                var client = _http.CreateClient();
                var url = $"https://graph.facebook.com/v19.0/{postId}"
                           + $"?fields=id,message,full_picture,created_time,permalink_url"
                           + $",likes.summary(true)"
                           + $",comments{{id,message,from,created_time,likes.summary(true),comments{{id,message,from,created_time}}}}"
                           + $",shares&access_token={pageToken}";

                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) { _logger.LogWarning("GetPostDetail: {b}", body); return null; }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var vm = new PostDetailViewModel { PostId = postId, PageId = pageId, Platform = "facebook" };

                if (root.TryGetProperty("message", out var msg)) vm.Message = msg.GetString();
                if (root.TryGetProperty("full_picture", out var pic)) vm.FullPicture = pic.GetString();
                if (root.TryGetProperty("permalink_url", out var pl)) vm.PermalinkUrl = pl.GetString();
                if (root.TryGetProperty("created_time", out var ct) &&
                    DateTime.TryParse(ct.GetString(), out var dt)) vm.CreatedTime = dt;
                vm.UpdatedAt = vm.CreatedTime?.ToUniversalTime() ?? DateTime.UtcNow;

                if (root.TryGetProperty("likes", out var likes) &&
                    likes.TryGetProperty("summary", out var ls) &&
                    ls.TryGetProperty("total_count", out var lc)) vm.LikesCount = lc.GetInt32();
                if (root.TryGetProperty("shares", out var shares) &&
                    shares.TryGetProperty("count", out var sc)) vm.SharesCount = sc.GetInt32();

                if (root.TryGetProperty("comments", out var cmts))
                {
                    if (cmts.TryGetProperty("summary", out var cs) &&
                        cs.TryGetProperty("total_count", out var cc)) vm.CommentsCount = cc.GetInt32();
                    if (cmts.TryGetProperty("data", out var cdata))
                    {
                        foreach (var c in cdata.EnumerateArray())
                        {
                            var cr = new CommentRowViewModel
                            {
                                CommentId = c.TryGetProperty("id", out var cid) ? cid.GetString() : "",
                                Message = c.TryGetProperty("message", out var cm) ? cm.GetString() : "",
                                SenderName = ""
                            };
                            if (c.TryGetProperty("from", out var from) && from.TryGetProperty("name", out var fn))
                                cr.SenderName = fn.GetString() ?? "Unknown";
                            if (c.TryGetProperty("created_time", out var cct) &&
                                DateTime.TryParse(cct.GetString(), out var cdt)) cr.CommentTime = cdt;
                            if (c.TryGetProperty("likes", out var cLikes) &&
                                cLikes.TryGetProperty("summary", out var cls) &&
                                cls.TryGetProperty("total_count", out var clc)) cr.LikesCount = clc.GetInt32();

                            if (c.TryGetProperty("comments", out var replies) &&
                                replies.TryGetProperty("data", out var rdata))
                            {
                                foreach (var r in rdata.EnumerateArray())
                                {
                                    var rr = new CommentRowViewModel
                                    {
                                        CommentId = r.TryGetProperty("id", out var rid) ? rid.GetString() : "",
                                        Message = r.TryGetProperty("message", out var rm) ? rm.GetString() : "",
                                        SenderName = ""
                                    };
                                    if (r.TryGetProperty("from", out var rf) && rf.TryGetProperty("name", out var rfn))
                                        rr.SenderName = rfn.GetString() ?? "Unknown";
                                    if (r.TryGetProperty("created_time", out var rct) &&
                                        DateTime.TryParse(rct.GetString(), out var rdt)) rr.CommentTime = rdt;
                                    cr.Replies.Add(rr);
                                }
                            }
                            vm.Comments.Add(cr);
                        }
                    }
                }

                // NOTE: post_impressions / post_reach were removed from the Graph API
                // for organic Page posts (confirmed live: "(#100) The value must be
                // a valid insights metric" as of the API version this app's tokens
                // are served — v23.0). Meta no longer exposes per-post reach/impressions
                // for unboosted posts, only engagement (likes/comments/shares, already
                // captured above) and a few metrics like post_clicks. Reach/Impressions
                // stay 0 for Facebook posts until/unless the post is boosted (see Ads).

                await _insightsSync.UpsertPostInsightAsync(
                    postId, pageId, "facebook",
                    likes: vm.LikesCount, comments: vm.CommentsCount, shares: vm.SharesCount,
                    reach: vm.Reach, impressions: vm.Impressions);

                return vm;
            }
            catch (Exception ex) { _logger.LogError("GetPostDetail ex: {m}", ex.Message); return null; }
        }

        // ── Instagram Post Detail ─────────────────────────────────────
        // Uses the linked Facebook Page's access token via graph.facebook.com —
        // graph.instagram.com requires a native Instagram Login token, which this
        // app never issues (it only implements Facebook Login + Page token).
        private async Task<PostDetailViewModel?> GetInstagramPostDetailAsync(string postId, string accessToken, string? igUserId = null)
        {
            if (string.IsNullOrEmpty(accessToken)) return null;

            try
            {
                var client = _http.CreateClient();
                var resp = await client.GetAsync(
                    $"https://graph.facebook.com/v19.0/{postId}"
                    + $"?fields=id,caption,media_url,permalink,timestamp,media_type,thumbnail_url,like_count,comments_count"
                    + $"&access_token={accessToken}");

                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) { _logger.LogError("IG detail: {b}", body); return null; }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var vm = new PostDetailViewModel { Platform = "instagram" };

                if (root.TryGetProperty("id", out var id)) vm.PostId = id.GetString() ?? "";
                if (root.TryGetProperty("caption", out var cap)) vm.Message = cap.GetString();
                if (root.TryGetProperty("media_url", out var mu)) vm.FullPicture = mu.GetString();
                if (root.TryGetProperty("thumbnail_url", out var thu)) vm.FullPicture ??= thu.GetString();
                if (root.TryGetProperty("permalink", out var pl)) vm.PermalinkUrl = pl.GetString();
                if (root.TryGetProperty("media_type", out var mt)) vm.MediaType = mt.GetString();
                if (root.TryGetProperty("timestamp", out var ts) &&
                    DateTime.TryParse(ts.GetString(), out var dt)) vm.CreatedTime = dt;
                vm.UpdatedAt = vm.CreatedTime?.ToUniversalTime() ?? DateTime.UtcNow;
                if (root.TryGetProperty("like_count", out var lc0)) vm.LikesCount = lc0.GetInt32();
                if (root.TryGetProperty("comments_count", out var cc0)) vm.CommentsCount = cc0.GetInt32();

                if (vm.MediaType == "CAROUSEL_ALBUM")
                {
                    var childResp = await client.GetAsync(
                        $"https://graph.facebook.com/v19.0/{postId}/children"
                        + $"?fields=id,media_url,thumbnail_url,media_type&access_token={accessToken}");
                    if (childResp.IsSuccessStatusCode)
                    {
                        var cb = await childResp.Content.ReadAsStringAsync();
                        using var cd = JsonDocument.Parse(cb);
                        if (cd.RootElement.TryGetProperty("data", out var children))
                        {
                            vm.CarouselChildren = new List<InstagramChildMedia>();
                            foreach (var child in children.EnumerateArray())
                            {
                                var item = new InstagramChildMedia();
                                if (child.TryGetProperty("id", out var cid)) item.Id = cid.GetString() ?? "";
                                if (child.TryGetProperty("media_url", out var cmu)) item.MediaUrl = cmu.GetString();
                                if (child.TryGetProperty("thumbnail_url", out var cth)) item.MediaUrl ??= cth.GetString();
                                if (child.TryGetProperty("media_type", out var cmt)) item.MediaType = cmt.GetString();
                                vm.CarouselChildren.Add(item);
                            }
                        }
                    }
                }

                try
                {
                    // reach/saved via graph.facebook.com + Page token
                    // (instagram_manage_insights). "impressions" deliberately
                    // excluded — confirmed live that Meta rejects it for media
                    // on current API versions ("impressions metric is no longer
                    // supported for the queried media" as of v22.0+).
                    var insResp = await client.GetAsync(
                        $"https://graph.facebook.com/v19.0/{postId}/insights"
                        + $"?metric=reach,saved&access_token={accessToken}");
                    if (insResp.IsSuccessStatusCode)
                    {
                        var ib = await insResp.Content.ReadAsStringAsync();
                        using var id2 = JsonDocument.Parse(ib);
                        if (id2.RootElement.TryGetProperty("data", out var insData))
                        {
                            foreach (var m in insData.EnumerateArray())
                            {
                                var mName = m.TryGetProperty("name", out var mn) ? mn.GetString() : "";
                                int val = 0;
                                if (m.TryGetProperty("values", out var mVals))
                                {
                                    var first = mVals.EnumerateArray().FirstOrDefault();
                                    if (first.ValueKind == JsonValueKind.Object &&
                                        first.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number)
                                        val = v.GetInt32();
                                }
                                else if (m.TryGetProperty("total_value", out var tv) &&
                                         tv.TryGetProperty("value", out var tvv) && tvv.ValueKind == JsonValueKind.Number)
                                    val = tvv.GetInt32();

                                switch (mName)
                                {
                                    case "reach": vm.Reach = val; break;
                                    case "impressions": vm.Impressions = val; break;
                                    case "saved": vm.SavesCount = val; break;
                                }
                            }
                        }
                    }
                }
                catch { /* optional */ }

                // Comments + replies
                vm.Comments = await GetInstagramCommentsAsync(postId, accessToken);

                await _insightsSync.UpsertPostInsightAsync(
                    postId, igUserId, "instagram",
                    likes: vm.LikesCount, comments: vm.CommentsCount,
                    reach: vm.Reach, impressions: vm.Impressions, saves: vm.SavesCount);

                return vm;
            }
            catch (Exception ex) { _logger.LogError("GetIGDetail ex: {m}", ex.Message); return null; }
        }

        // ── Instagram Comments with nested replies ────────────────────
        // graph.facebook.com + Page token (not graph.instagram.com — see note above).
        private async Task<List<CommentRowViewModel>> GetInstagramCommentsAsync(string mediaId, string token)
        {
            var list = new List<CommentRowViewModel>();
            var client = _http.CreateClient();
            var resp = await client.GetAsync(
                $"https://graph.facebook.com/v19.0/{mediaId}/comments"
                + $"?fields=id,text,username,timestamp,replies{{id,text,username,timestamp}}"
                + $"&access_token={token}");

            if (!resp.IsSuccessStatusCode) return list;

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return list;

            foreach (var c in data.EnumerateArray())
            {
                var cm = new CommentRowViewModel
                {
                    CommentId = c.TryGetProperty("id", out var cid) ? cid.GetString() : "",
                    Message = c.TryGetProperty("text", out var ctxt) ? ctxt.GetString() : "",
                    SenderName = c.TryGetProperty("username", out var cun) ? cun.GetString() ?? "" : ""
                };
                if (c.TryGetProperty("timestamp", out var cts) &&
                    DateTime.TryParse(cts.GetString(), out var cdt)) cm.CommentTime = cdt;

                if (c.TryGetProperty("replies", out var repliesObj) &&
                    repliesObj.TryGetProperty("data", out var rdata))
                {
                    foreach (var r in rdata.EnumerateArray())
                    {
                        var rr = new CommentRowViewModel
                        {
                            CommentId = r.TryGetProperty("id", out var rid) ? rid.GetString() : "",
                            Message = r.TryGetProperty("text", out var rtxt) ? rtxt.GetString() : "",
                            SenderName = r.TryGetProperty("username", out var run) ? run.GetString() ?? "" : ""
                        };
                        if (r.TryGetProperty("timestamp", out var rts) &&
                            DateTime.TryParse(rts.GetString(), out var rdt)) rr.CommentTime = rdt;
                        cm.Replies.Add(rr);
                    }
                }
                list.Add(cm);
            }
            return list;
        }

        // ════════════════════════════════════════════════════════════
        // 4. LIKE (Facebook only)
        // ════════════════════════════════════════════════════════════
        public async Task<bool> LikePostAsync(string postId, int userId, string userEmail, string? pageId)
        {
            var resolved = await ResolveFacebookPageAsync(userId, pageId, userEmail);
            if (resolved == null) return false;
            try
            {
                var client = _http.CreateClient();
                var resp = await client.PostAsync(
                    $"https://graph.facebook.com/v19.0/{postId}/likes?access_token={resolved.Value.pageToken}", null);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex) { _logger.LogError("Like ex: {m}", ex.Message); return false; }
        }

        // ════════════════════════════════════════════════════════════
        // 5. ADD COMMENT — Facebook + Instagram
        // Both go through graph.facebook.com + the relevant Page Access Token
        // (Instagram via Facebook Login — graph.instagram.com is a different,
        // unimplemented auth flow and was never actually reachable here).
        // ════════════════════════════════════════════════════════════
        public async Task<bool> AddCommentAsync(string postId, string message, int userId, string userEmail, string? pageId, string platform = "facebook")
        {
            if (platform.ToLower() == "instagram")
            {
                var igResolved = await ResolveInstagramAccountAsync(userId, pageId);
                if (igResolved == null) return false;
                try
                {
                    var client = _http.CreateClient();
                    var url = $"https://graph.facebook.com/v19.0/{postId}/comments"
                               + $"?message={Uri.EscapeDataString(message)}"
                               + $"&access_token={igResolved.Value.pageToken}";
                    var resp = await client.PostAsync(url, null);
                    var body2 = await resp.Content.ReadAsStringAsync();
                    _logger.LogInformation("IG Comment response: {b}", body2);
                    return resp.IsSuccessStatusCode;
                }
                catch (Exception ex) { _logger.LogError("IG Comment ex: {m}", ex.Message); return false; }
            }

            // Facebook
            var resolved = await ResolveFacebookPageAsync(userId, pageId, userEmail);
            if (resolved == null) return false;
            try
            {
                var client = _http.CreateClient();
                var resp = await client.PostAsync(
                    $"https://graph.facebook.com/v19.0/{postId}/comments"
                    + $"?message={Uri.EscapeDataString(message)}"
                    + $"&access_token={resolved.Value.pageToken}", null);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex) { _logger.LogError("FB Comment ex: {m}", ex.Message); return false; }
        }

        // ════════════════════════════════════════════════════════════
        // MODERATION — delete / hide a comment (Facebook + Instagram)
        // Requires pages_manage_engagement / instagram_manage_comments.
        // ════════════════════════════════════════════════════════════
        public async Task<bool> DeleteCommentAsync(string commentId, int userId, string? pageId, string platform = "facebook")
        {
            var token = await ResolveCommentTokenAsync(userId, pageId, platform);
            if (string.IsNullOrEmpty(token)) return false;
            try
            {
                var client = _http.CreateClient();
                var resp = await client.DeleteAsync(
                    $"https://graph.facebook.com/v19.0/{commentId}?access_token={token}");
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex) { _logger.LogError("DeleteComment ex: {m}", ex.Message); return false; }
        }

        public async Task<bool> HideCommentAsync(string commentId, bool hide, int userId, string? pageId, string platform = "facebook")
        {
            var token = await ResolveCommentTokenAsync(userId, pageId, platform);
            if (string.IsNullOrEmpty(token)) return false;
            try
            {
                var client = _http.CreateClient();
                var resp = await client.PostAsync(
                    // Graph API param is "hide", not "is_hidden" — confirmed live
                    // ("(#100) The parameter hide is required.").
                    $"https://graph.facebook.com/v19.0/{commentId}?hide={(hide ? "true" : "false")}&access_token={token}", null);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex) { _logger.LogError("HideComment ex: {m}", ex.Message); return false; }
        }

        private async Task<string?> ResolveCommentTokenAsync(int userId, string? pageId, string platform)
        {
            if (platform.ToLower() == "instagram")
            {
                var igResolved = await ResolveInstagramAccountAsync(userId, pageId);
                return igResolved?.pageToken;
            }
            var resolved = await ResolveFacebookPageAsync(userId, pageId, "");
            return resolved?.pageToken;
        }


        //public async Task<PostListViewModel> GetDraftPostsAsync(int userId, string platform)
        //{
        //    var drafts = await _db.Set<SocialPost>()     // ya jo bhi tera DbSet name hai
        //        .Where(p => p.user_id == userId
        //                 && p.status == "draft"
        //                 && (platform == "all" || p.platform == platform))
        //        .OrderByDescending(p => p.created_at)
        //        .ToListAsync();

        //    var rows = drafts.Select(p => new PostRowViewModel
        //    {
        //        PostId = p.post_id ?? p.id.ToString(),
        //        Platform = p.platform ?? platform,
        //        Message = p.message,
        //        UpdatedAt = p.created_at,
        //    }).ToList();

        //    return new PostListViewModel
        //    {
        //        Posts = rows,
        //        ActivePlatform = platform,
        //        ActiveTab = "draft"
        //    };
        //}

        // ════════════════════════════════════════════════════════════
        // 6. REPLY TO COMMENT — Facebook + Instagram
        // ════════════════════════════════════════════════════════════
        public async Task<bool> ReplyToCommentAsync(string commentId, string message, int userId, string userEmail, string? pageId, string platform = "facebook")
        {
            if (platform.ToLower() == "instagram")
            {
                var igResolved = await ResolveInstagramAccountAsync(userId, pageId);
                if (igResolved == null) return false;
                try
                {
                    var client = _http.CreateClient();
                    var url = $"https://graph.facebook.com/v19.0/{commentId}/replies"
                               + $"?message={Uri.EscapeDataString(message)}"
                               + $"&access_token={igResolved.Value.pageToken}";
                    var resp = await client.PostAsync(url, null);
                    var body2 = await resp.Content.ReadAsStringAsync();
                    _logger.LogInformation("IG Reply response: {b}", body2);
                    return resp.IsSuccessStatusCode;
                }
                catch (Exception ex) { _logger.LogError("IG Reply ex: {m}", ex.Message); return false; }
            }

            // Facebook
            var resolved2 = await ResolveFacebookPageAsync(userId, pageId, userEmail);
            if (resolved2 == null) return false;
            try
            {
                var client = _http.CreateClient();
                var resp = await client.PostAsync(
                    $"https://graph.facebook.com/v19.0/{commentId}/comments"
                    + $"?message={Uri.EscapeDataString(message)}"
                    + $"&access_token={resolved2.Value.pageToken}", null);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex) { _logger.LogError("FB Reply ex: {m}", ex.Message); return false; }
        }
    }
}