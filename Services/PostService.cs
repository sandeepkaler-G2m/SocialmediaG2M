//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Net.Http;
//using System.Text.Json;
//using System.Threading.Tasks;
//using Microsoft.EntityFrameworkCore;
//using Microsoft.Extensions.Logging;
//using SocialMediaPanel.Data;
//using SocialMediaPanel.ViewModels;

//namespace SocialMediaPanel.Services
//{
//    public interface IPostService
//    {
//        Task<PostListViewModel> GetPostsAsync(string userEmail, string platform = "facebook");
//        Task<PostListViewModel> GetInstagramPostsAsync(string userEmail);
//        Task<PostDetailViewModel?> GetPostDetailAsync(string postId, string userEmail,string platform);
//        Task<bool> LikePostAsync(string postId, string userEmail);
//        Task<bool> AddCommentAsync(string postId, string message, string userEmail);
//        Task<bool> ReplyToCommentAsync(string commentId, string message, string userEmail); // ← NEW
//    }

//    public class PostService : IPostService
//    {
//        private readonly AppDbContext _db;
//        private readonly IHttpClientFactory _http;
//        private readonly ILogger<PostService> _logger;

//        public PostService(AppDbContext db, IHttpClientFactory http, ILogger<PostService> logger)
//        {
//            _db = db;
//            _http = http;
//            _logger = logger;
//        }

//        // ── Token fetch ───────────────────────────────────────────────
//        private async Task<string?> GetTokenAsync(string userEmail)
//        {
//            var row = await _db.UserTokens
//                .Where(t => t.username == userEmail
//                         && t.facebooktoken != null
//                         && t.facebooktoken != "")
//                .OrderByDescending(t => t.CreatedAt)
//                .FirstOrDefaultAsync();

//            if (row == null) { _logger.LogWarning("No token: {e}", userEmail); return null; }
//            return row.facebooktoken;
//        }

//        private async Task<string?> GetInstagramTokenAsync(string userEmail)
//        {
//            // Adjust table/column names to match your existing schema
//            var account = await _db.UserTokens
//                .Where(t => t.username == userEmail
//                         && t.instagramtoken != null
//                         && t.instagramtoken != "")
//                .OrderByDescending(t => t.CreatedAt)
//                .FirstOrDefaultAsync();

//            return account?.instagramtoken;
//        }
//        public async Task<PostListViewModel> GetInstagramPostsAsync(string userEmail)
//        {
//            var accessToken = await GetInstagramTokenAsync(userEmail);
//            if (string.IsNullOrEmpty(accessToken))
//                return new PostListViewModel { ActivePlatform = "instagram" };

//            try
//            {
//                var client = _http.CreateClient();
//                var url = "https://graph.instagram.com/me/media"
//                        + "?fields=id,caption,media_url,permalink,timestamp"
//                        + $"&access_token={accessToken}";

//                var resp = await client.GetAsync(url);
//                var body = await resp.Content.ReadAsStringAsync();

//                if (!resp.IsSuccessStatusCode)
//                {
//                    _logger.LogError("Instagram API error: {body}", body);
//                    return new PostListViewModel { ActivePlatform = "instagram" };
//                }

//                using var doc = JsonDocument.Parse(body);
//                var rows = new List<PostRowViewModel>();

//                if (doc.RootElement.TryGetProperty("data", out var data))
//                {
//                    foreach (var post in data.EnumerateArray())
//                    {
//                        var row = new PostRowViewModel
//                        {
//                            Platform = "instagram",
//                        };

//                        if (post.TryGetProperty("id", out var pid))
//                            row.PostId = pid.GetString() ?? "";

//                        if (post.TryGetProperty("caption", out var cap))
//                            row.Message = cap.GetString();

//                        if (post.TryGetProperty("media_url", out var mu))
//                            row.FullPicture = mu.GetString();

//                        if (post.TryGetProperty("permalink", out var pl))
//                            row.PermalinkUrl = pl.GetString();

//                        if (post.TryGetProperty("timestamp", out var ts) &&
//                            DateTime.TryParse(ts.GetString(), out var dt))
//                            row.CreatedTime = dt;

//                        row.UpdatedAt = row.CreatedTime?.ToUniversalTime() ?? DateTime.UtcNow;
//                        rows.Add(row);
//                    }
//                }

//                return new PostListViewModel
//                {
//                    Posts = rows,
//                    ActivePlatform = "instagram",
//                    ActiveTab = "published"
//                };
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError("GetInstagramPosts ex: {m}", ex.Message);
//                return new PostListViewModel { ActivePlatform = "instagram" };
//            }
//        }

//        // ── Page ID + Page Token ──────────────────────────────────────
//        private async Task<(string pageId, string pageToken)?> GetFirstPageAsync(string userToken)
//        {
//            try
//            {
//                var client = _http.CreateClient();
//                var resp = await client.GetAsync(
//                    $"https://graph.facebook.com/v19.0/me/accounts"
//                    + $"?fields=id,name,access_token&access_token={userToken}");

//                var body = await resp.Content.ReadAsStringAsync();
//                if (!resp.IsSuccessStatusCode) return null;

//                using var doc = JsonDocument.Parse(body);
//                if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

//                var pages = data.EnumerateArray().ToList();
//                if (!pages.Any()) return null;

//                var first = pages[0];
//                var pageId = first.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
//                var pageToken = first.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";

//                if (string.IsNullOrEmpty(pageId) || string.IsNullOrEmpty(pageToken)) return null;
//                return (pageId, pageToken);
//            }
//            catch (Exception ex) { _logger.LogError("GetPage ex: {m}", ex.Message); return null; }
//        }

//        // ════════════════════════════════════════════════════════════
//        // 1. POSTS LIST
//        // ════════════════════════════════════════════════════════════
//        public async Task<PostListViewModel> GetPostsAsync(string userEmail, string platform = "facebook")
//        {
//            var userToken = await GetTokenAsync(userEmail);
//            if (string.IsNullOrEmpty(userToken))
//                return new PostListViewModel { ActivePlatform = platform };

//            var page = await GetFirstPageAsync(userToken);
//            if (page == null)
//                return new PostListViewModel { ActivePlatform = platform };

//            var (pageId, pageToken) = page.Value;

//            try
//            {
//                var client = _http.CreateClient();
//                var url = $"https://graph.facebook.com/v19.0/{pageId}/posts"
//                           + $"?fields=id,message,full_picture,created_time,permalink_url"
//                           + $",likes.summary(true),comments.summary(true),shares"
//                           + $"&limit=25&access_token={pageToken}";

//                var resp = await client.GetAsync(url);
//                var body = await resp.Content.ReadAsStringAsync();

//                if (!resp.IsSuccessStatusCode)
//                    return new PostListViewModel { ActivePlatform = platform };

//                using var doc = JsonDocument.Parse(body);
//                var rows = new List<PostRowViewModel>();

//                if (doc.RootElement.TryGetProperty("data", out var data))
//                {
//                    foreach (var post in data.EnumerateArray())
//                    {
//                        var row = new PostRowViewModel
//                        {
//                            PostId = post.TryGetProperty("id", out var pid) ? pid.GetString() ?? "" : "",
//                            PageId = pageId,
//                            Platform = platform,
//                        };

//                        if (post.TryGetProperty("message", out var msg)) row.Message = msg.GetString();
//                        if (post.TryGetProperty("full_picture", out var pic)) row.FullPicture = pic.GetString();
//                        if (post.TryGetProperty("permalink_url", out var pl)) row.PermalinkUrl = pl.GetString();
//                        if (post.TryGetProperty("created_time", out var ct) &&
//                            DateTime.TryParse(ct.GetString(), out var dt)) row.CreatedTime = dt;

//                        if (post.TryGetProperty("likes", out var likes) &&
//                            likes.TryGetProperty("summary", out var ls) &&
//                            ls.TryGetProperty("total_count", out var lc))
//                            row.LikesCount = lc.GetInt32();

//                        if (post.TryGetProperty("comments", out var cmts) &&
//                            cmts.TryGetProperty("summary", out var cs) &&
//                            cs.TryGetProperty("total_count", out var cc))
//                            row.CommentsCount = cc.GetInt32();

//                        if (post.TryGetProperty("shares", out var sh) &&
//                            sh.TryGetProperty("count", out var sc))
//                            row.SharesCount = sc.GetInt32();

//                        row.UpdatedAt = row.CreatedTime?.ToUniversalTime() ?? DateTime.UtcNow;
//                        rows.Add(row);
//                    }
//                }

//                return new PostListViewModel { Posts = rows, ActivePlatform = platform, ActiveTab = "published" };
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError("GetPosts ex: {m}", ex.Message);
//                return new PostListViewModel { ActivePlatform = platform };
//            }
//        }

//        // ════════════════════════════════════════════════════════════
//        // 2. POST DETAIL — message, image, likes, comments with replies
//        // ════════════════════════════════════════════════════════════
//        public async Task<PostDetailViewModel?> GetPostDetailAsync(string postId, string userEmail,string platform)
//        {
//            var userToken = await GetTokenAsync(userEmail);
//            if (string.IsNullOrEmpty(userToken)) return null;

//            var page = await GetFirstPageAsync(userToken);
//            if (page == null) return null;

//            var (pageId, pageToken) = page.Value;

//            if (platform.ToLower() == "instagram")
//            {
//                return await GetInstagramPostDetailAsync(postId, userEmail);
//            }


//            try
//            {
//                var client = _http.CreateClient();

//                // comments mein replies bhi fetch karo
//                var url = $"https://graph.facebook.com/v19.0/{postId}"
//                        + $"?fields=id,message,full_picture,created_time,permalink_url"
//                        + $",likes.summary(true)"
//                        + $",comments{{id,message,from,created_time,likes.summary(true),comments{{id,message,from,created_time}}}}"
//                        + $",shares"
//                        + $"&access_token={pageToken}";

//                var resp = await client.GetAsync(url);
//                var body = await resp.Content.ReadAsStringAsync();

//                if (!resp.IsSuccessStatusCode)
//                {
//                    _logger.LogWarning("GetPostDetail failed: {b}", body);
//                    return null;
//                }

//                using var doc = JsonDocument.Parse(body);
//                var root = doc.RootElement;

//                var vm = new PostDetailViewModel
//                {
//                    PostId = postId,
//                    PageId = pageId,
//                    Platform = "facebook",
//                };

//                if (root.TryGetProperty("message", out var msg)) vm.Message = msg.GetString();
//                if (root.TryGetProperty("full_picture", out var pic)) vm.FullPicture = pic.GetString();
//                if (root.TryGetProperty("permalink_url", out var pl)) vm.PermalinkUrl = pl.GetString();
//                if (root.TryGetProperty("created_time", out var ct) &&
//                    DateTime.TryParse(ct.GetString(), out var dt)) vm.CreatedTime = dt;

//                vm.UpdatedAt = vm.CreatedTime?.ToUniversalTime() ?? DateTime.UtcNow;

//                if (root.TryGetProperty("likes", out var likes) &&
//                    likes.TryGetProperty("summary", out var ls) &&
//                    ls.TryGetProperty("total_count", out var lc))
//                    vm.LikesCount = lc.GetInt32();

//                if (root.TryGetProperty("shares", out var shares) &&
//                    shares.TryGetProperty("count", out var sc))
//                    vm.SharesCount = sc.GetInt32();

//                // Comments + nested replies
//                if (root.TryGetProperty("comments", out var cmts))
//                {
//                    if (cmts.TryGetProperty("summary", out var cs) &&
//                        cs.TryGetProperty("total_count", out var cc))
//                        vm.CommentsCount = cc.GetInt32();

//                    if (cmts.TryGetProperty("data", out var cdata))
//                    {
//                        foreach (var c in cdata.EnumerateArray())
//                        {
//                            var cr = new CommentRowViewModel();
//                            cr.CommentId = c.TryGetProperty("id", out var cid) ? cid.GetString() : "";
//                            cr.Message = c.TryGetProperty("message", out var cm) ? cm.GetString() : "";
//                            cr.SenderName = "";

//                            if (c.TryGetProperty("from", out var from) &&
//                                from.TryGetProperty("name", out var fn))
//                                cr.SenderName = fn.GetString() ?? "Unknown";

//                            if (c.TryGetProperty("created_time", out var cct) &&
//                                DateTime.TryParse(cct.GetString(), out var cdt))
//                                cr.CommentTime = cdt;

//                            // Comment likes
//                            if (c.TryGetProperty("likes", out var cLikes) &&
//                                cLikes.TryGetProperty("summary", out var cls) &&
//                                cls.TryGetProperty("total_count", out var clc))
//                                cr.LikesCount = clc.GetInt32();

//                            // Nested replies
//                            if (c.TryGetProperty("comments", out var replies) &&
//                                replies.TryGetProperty("data", out var rdata))
//                            {
//                                foreach (var r in rdata.EnumerateArray())
//                                {
//                                    var rr = new CommentRowViewModel();
//                                    rr.CommentId = r.TryGetProperty("id", out var rid) ? rid.GetString() : "";
//                                    rr.Message = r.TryGetProperty("message", out var rm) ? rm.GetString() : "";
//                                    rr.SenderName = "";

//                                    if (r.TryGetProperty("from", out var rfrom) &&
//                                        rfrom.TryGetProperty("name", out var rfn))
//                                        rr.SenderName = rfn.GetString() ?? "Unknown";

//                                    if (r.TryGetProperty("created_time", out var rct) &&
//                                        DateTime.TryParse(rct.GetString(), out var rdt))
//                                        rr.CommentTime = rdt;

//                                    cr.Replies.Add(rr);
//                                }
//                            }

//                            vm.Comments.Add(cr);
//                        }
//                    }
//                }

//                // Insights
//                try
//                {
//                    var insResp = await client.GetAsync(
//                        $"https://graph.facebook.com/v19.0/{postId}/insights"
//                        + $"?metric=post_impressions,post_reach"
//                        + $"&access_token={pageToken}");

//                    if (insResp.IsSuccessStatusCode)
//                    {
//                        var insBody = await insResp.Content.ReadAsStringAsync();
//                        using var insDoc = JsonDocument.Parse(insBody);
//                        if (insDoc.RootElement.TryGetProperty("data", out var insData))
//                        {
//                            foreach (var m in insData.EnumerateArray())
//                            {
//                                var mName = m.TryGetProperty("name", out var mn) ? mn.GetString() : "";
//                                if (m.TryGetProperty("values", out var mVals))
//                                {
//                                    var last = mVals.EnumerateArray().LastOrDefault();
//                                    if (last.TryGetProperty("value", out var v) &&
//                                        v.ValueKind == JsonValueKind.Number)
//                                    {
//                                        if (mName == "post_impressions") vm.Impressions = v.GetInt32();
//                                        if (mName == "post_reach") vm.Reach = v.GetInt32();
//                                    }
//                                }
//                            }
//                        }
//                    }
//                }
//                catch { /* insights optional */ }

//                return vm;
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError("GetPostDetail ex: {m}", ex.Message);
//                return null;
//            }
//        }

//        // ── Instagram detail ─────────────────────────────────────────────
//        private async Task<PostDetailViewModel?> GetInstagramPostDetailAsync(string postId, string userEmail)
//        {
//            var accessToken = await GetInstagramTokenAsync(userEmail);
//            if (string.IsNullOrEmpty(accessToken)) return null;
//            try
//            {
//                var client = _http.CreateClient();
//                var url = $"https://graph.instagram.com/{postId}"
//                        + $"?fields=id,caption,media_url,permalink,timestamp,media_type,thumbnail_url"
//                        + $"&access_token={accessToken}";
//                var resp = await client.GetAsync(url);
//                var body = await resp.Content.ReadAsStringAsync();
//                if (!resp.IsSuccessStatusCode)
//                {
//                    _logger.LogError("Instagram detail error: {b}", body);
//                    return null;
//                }
//                using var doc = JsonDocument.Parse(body);
//                var root = doc.RootElement;
//                var vm = new PostDetailViewModel { Platform = "instagram" };
//                if (root.TryGetProperty("id", out var id)) vm.PostId = id.GetString() ?? "";
//                if (root.TryGetProperty("caption", out var cap)) vm.Message = cap.GetString();
//                if (root.TryGetProperty("media_url", out var mu)) vm.FullPicture = mu.GetString();
//                if (root.TryGetProperty("thumbnail_url", out var thu)) vm.FullPicture ??= thu.GetString();
//                if (root.TryGetProperty("permalink", out var pl)) vm.PermalinkUrl = pl.GetString();
//                if (root.TryGetProperty("media_type", out var mt)) vm.MediaType = mt.GetString();
//                if (root.TryGetProperty("timestamp", out var ts) &&
//                    DateTime.TryParse(ts.GetString(), out var dt)) vm.CreatedTime = dt;
//                vm.UpdatedAt = vm.CreatedTime?.ToUniversalTime() ?? DateTime.UtcNow;

//                // Carousel → fetch children
//                if (vm.MediaType == "CAROUSEL_ALBUM")
//                {
//                    var childResp = await client.GetAsync(
//                        $"https://graph.instagram.com/{postId}/children"
//                        + $"?fields=id,media_url,thumbnail_url,media_type"
//                        + $"&access_token={accessToken}");
//                    if (childResp.IsSuccessStatusCode)
//                    {
//                        var childBody = await childResp.Content.ReadAsStringAsync();
//                        using var childDoc = JsonDocument.Parse(childBody);
//                        if (childDoc.RootElement.TryGetProperty("data", out var children))
//                        {
//                            vm.CarouselChildren = new List<InstagramChildMedia>();
//                            foreach (var child in children.EnumerateArray())
//                            {
//                                var item = new InstagramChildMedia();
//                                if (child.TryGetProperty("id", out var cid)) item.Id = cid.GetString() ?? "";
//                                if (child.TryGetProperty("media_url", out var cmu)) item.MediaUrl = cmu.GetString();
//                                if (child.TryGetProperty("thumbnail_url", out var ct)) item.MediaUrl ??= ct.GetString();
//                                if (child.TryGetProperty("media_type", out var cmt)) item.MediaType = cmt.GetString();
//                                vm.CarouselChildren.Add(item);
//                            }
//                        }
//                    }
//                }

//                // ── Insights ──────────────────────────────────────────────
//                try
//                {
//                    var insResp = await client.GetAsync(
//                        $"https://graph.instagram.com/v25.0/{postId}/insights"
//                        + $"?metric=likes,comments,reach,shares,saved"
//                        + $"&access_token={accessToken}");

//                    if (insResp.IsSuccessStatusCode)
//                    {
//                        var insBody = await insResp.Content.ReadAsStringAsync();
//                        using var insDoc = JsonDocument.Parse(insBody);

//                        if (insDoc.RootElement.TryGetProperty("data", out var insData))
//                        {
//                            foreach (var metric in insData.EnumerateArray())
//                            {
//                                var mName = metric.TryGetProperty("name", out var mn) ? mn.GetString() : "";

//                                // Instagram insights return "values" array like Facebook
//                                if (metric.TryGetProperty("values", out var mVals))
//                                {
//                                    var first = mVals.EnumerateArray().FirstOrDefault();
//                                    if (first.ValueKind == JsonValueKind.Object &&
//                                        first.TryGetProperty("value", out var val) &&
//                                        val.ValueKind == JsonValueKind.Number)
//                                    {
//                                        switch (mName)
//                                        {
//                                            case "likes": vm.LikesCount = val.GetInt32(); break;
//                                            case "comments": vm.CommentsCount = val.GetInt32(); break;
//                                            case "reach": vm.Reach = val.GetInt32(); break;
//                                            case "shares": vm.SharesCount = val.GetInt32(); break;
//                                            //case "saved": vm.SavedCount = val.GetInt32(); break;
//                                        }
//                                    }
//                                }
//                                // Some versions return "total_value" instead of "values"
//                                else if (metric.TryGetProperty("total_value", out var tv) &&
//                                         tv.TryGetProperty("value", out var tvVal) &&
//                                         tvVal.ValueKind == JsonValueKind.Number)
//                                {
//                                    switch (mName)
//                                    {
//                                        case "likes": vm.LikesCount = tvVal.GetInt32(); break;
//                                        case "comments": vm.CommentsCount = tvVal.GetInt32(); break;
//                                        case "reach": vm.Reach = tvVal.GetInt32(); break;
//                                        case "shares": vm.SharesCount = tvVal.GetInt32(); break;
//                                        //case "saved": vm.SavedCount = tvVal.GetInt32(); break;
//                                    }
//                                }
//                            }
//                        }
//                    }
//                    else
//                    {
//                        var errBody = await insResp.Content.ReadAsStringAsync();
//                        _logger.LogWarning("Instagram insights failed: {b}", errBody);
//                    }
//                }
//                catch { /* insights optional — detail still loads */ }
//                vm.Comments = await GetInstagramComments(postId, accessToken);

//                return vm;
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError("GetInstagramPostDetail ex: {m}", ex.Message);
//                return null;
//            }
//        }
//        // ── 3. LIKE POST ──────────────────────────────────────────────
//        public async Task<bool> LikePostAsync(string postId, string userEmail)
//        {
//            var userToken = await GetTokenAsync(userEmail);
//            if (string.IsNullOrEmpty(userToken)) return false;
//            var page = await GetFirstPageAsync(userToken);
//            if (page == null) return false;
//            try
//            {
//                var client = _http.CreateClient();
//                var resp = await client.PostAsync(
//                    $"https://graph.facebook.com/v19.0/{postId}/likes?access_token={page.Value.pageToken}", null);
//                return resp.IsSuccessStatusCode;
//            }
//            catch (Exception ex) { _logger.LogError("Like ex: {m}", ex.Message); return false; }
//        }

//        // ── 4. ADD COMMENT ────────────────────────────────────────────
//        public async Task<bool> AddCommentAsync(string postId, string message, string userEmail)
//        {
//            var userToken = await GetTokenAsync(userEmail);
//            if (string.IsNullOrEmpty(userToken)) return false;
//            var page = await GetFirstPageAsync(userToken);
//            if (page == null) return false;
//            try
//            {
//                var client = _http.CreateClient();
//                var resp = await client.PostAsync(
//                    $"https://graph.facebook.com/v19.0/{postId}/comments"
//                    + $"?message={Uri.EscapeDataString(message)}"
//                    + $"&access_token={page.Value.pageToken}", null);
//                return resp.IsSuccessStatusCode;
//            }
//            catch (Exception ex) { _logger.LogError("Comment ex: {m}", ex.Message); return false; }
//        }

//        private async Task<List<CommentRowViewModel>> GetInstagramComments(string mediaId, string token)
//        {
//            var list = new List<CommentRowViewModel>();
//            var client = _http.CreateClient();

//            var url = $"https://graph.instagram.com/v25.0/{mediaId}/comments?fields=id,text,username,timestamp&access_token={token}";
//            var resp = await client.GetAsync(url);

//            if (!resp.IsSuccessStatusCode) return list;

//            var body = await resp.Content.ReadAsStringAsync();
//            using var doc = JsonDocument.Parse(body);

//            if (doc.RootElement.TryGetProperty("data", out var data))
//            {
//                foreach (var c in data.EnumerateArray())
//                {
//                    var cm = new CommentRowViewModel
//                    {
//                        CommentId = c.GetProperty("id").GetString(),
//                        Message = c.GetProperty("text").GetString(),
//                        SenderName = c.GetProperty("username").GetString()
//                    };

//                    if (c.TryGetProperty("timestamp", out var ts) &&
//                        DateTime.TryParse(ts.GetString(), out var dt))
//                        cm.CommentTime = dt;

//                    list.Add(cm);
//                }
//            }

//            return list;
//        }

//        public async Task<bool> ReplyToCommentAsync(string commentId, string message, string userEmail)
//        {
//            var token = await GetInstagramTokenAsync(userEmail);
//            if (string.IsNullOrEmpty(token)) return false;

//            var client = _http.CreateClient();

//            var url = $"https://graph.instagram.com/v25.0/{commentId}/replies";

//            var content = new FormUrlEncodedContent(new[]
//            {
//        new KeyValuePair<string, string>("message", message),
//        new KeyValuePair<string, string>("access_token", token)
//    });

//            var resp = await client.PostAsync(url, content);
//            return resp.IsSuccessStatusCode;
//        }

//        // ── 5. REPLY TO COMMENT ───────────────────────────────────────
//        // commentId ke /comments endpoint pe POST karo
//        //public async Task<bool> ReplyToCommentAsync(string commentId, string message, string userEmail)
//        //{
//        //    var userToken = await GetTokenAsync(userEmail);
//        //    if (string.IsNullOrEmpty(userToken)) return false;
//        //    var page = await GetFirstPageAsync(userToken);
//        //    if (page == null) return false;
//        //    try
//        //    {
//        //        var client = _http.CreateClient();
//        //        var resp = await client.PostAsync(
//        //            $"https://graph.facebook.com/v19.0/{commentId}/comments"
//        //            + $"?message={Uri.EscapeDataString(message)}"
//        //            + $"&access_token={page.Value.pageToken}", null);
//        //        return resp.IsSuccessStatusCode;
//        //    }
//        //    catch (Exception ex) { _logger.LogError("Reply ex: {m}", ex.Message); return false; }
//        //}
//    }
//}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SocialMediaPanel.Data;
using SocialMediaPanel.ViewModels;

namespace SocialMediaPanel.Services
{
    public interface IPostService
    {
        Task<string?> GetTokenAsync(string userEmail);
        Task<string?> GetInstagramTokenAsync(string userEmail);
        Task<PostListViewModel> GetPostsAsync(string userEmail, string platform = "facebook");
        Task<PostListViewModel> GetInstagramPostsAsync(string userEmail);
        Task<PostDetailViewModel?> GetPostDetailAsync(string postId, string userEmail, string platform);
        Task<bool> LikePostAsync(string postId, string userEmail);
        Task<bool> AddCommentAsync(string postId, string message, string userEmail, string platform = "facebook");
        Task<bool> ReplyToCommentAsync(string commentId, string message, string userEmail, string platform = "facebook");
    }

    public class PostService : IPostService
    {
        private readonly AppDbContext _db;
        private readonly IHttpClientFactory _http;
        private readonly ILogger<PostService> _logger;

        public PostService(AppDbContext db, IHttpClientFactory http, ILogger<PostService> logger)
        {
            _db = db;
            _http = http;
            _logger = logger;
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
        public async Task<PostListViewModel> GetPostsAsync(string userEmail, string platform = "facebook")
        {
            var userToken = await GetTokenAsync(userEmail);
            if (string.IsNullOrEmpty(userToken))
                return new PostListViewModel { ActivePlatform = platform };

            var page = await GetFirstPageAsync(userToken);
            if (page == null)
                return new PostListViewModel { ActivePlatform = platform };

            var (pageId, pageToken) = page.Value;

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

        // ════════════════════════════════════════════════════════════
        // 2. INSTAGRAM POSTS LIST
        // ════════════════════════════════════════════════════════════
        public async Task<PostListViewModel> GetInstagramPostsAsync(string userEmail)
        {
            var accessToken = await GetInstagramTokenAsync(userEmail);
            if (string.IsNullOrEmpty(accessToken))
                return new PostListViewModel { ActivePlatform = "instagram" };

            try
            {
                var client = _http.CreateClient();
                var url = "https://graph.instagram.com/me/media"
                           + "?fields=id,caption,media_url,permalink,timestamp"
                           + $"&access_token={accessToken}";

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
        public async Task<PostDetailViewModel?> GetPostDetailAsync(string postId, string userEmail, string platform)
        {
            if (platform.ToLower() == "instagram")
                return await GetInstagramPostDetailAsync(postId, userEmail);

            // ── Facebook ──────────────────────────────────────────────
            var userToken = await GetTokenAsync(userEmail);
            if (string.IsNullOrEmpty(userToken)) return null;
            var page = await GetFirstPageAsync(userToken);
            if (page == null) return null;
            var (pageId, pageToken) = page.Value;

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

                try
                {
                    var insResp = await client.GetAsync(
                        $"https://graph.facebook.com/v19.0/{postId}/insights"
                        + $"?metric=post_impressions,post_reach&access_token={pageToken}");
                    if (insResp.IsSuccessStatusCode)
                    {
                        var insBody = await insResp.Content.ReadAsStringAsync();
                        using var insDoc = JsonDocument.Parse(insBody);
                        if (insDoc.RootElement.TryGetProperty("data", out var insData))
                        {
                            foreach (var m in insData.EnumerateArray())
                            {
                                var mName = m.TryGetProperty("name", out var mn) ? mn.GetString() : "";
                                if (m.TryGetProperty("values", out var mVals))
                                {
                                    var last = mVals.EnumerateArray().LastOrDefault();
                                    if (last.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number)
                                    {
                                        if (mName == "post_impressions") vm.Impressions = v.GetInt32();
                                        if (mName == "post_reach") vm.Reach = v.GetInt32();
                                    }
                                }
                            }
                        }
                    }
                }
                catch { /* optional */ }

                return vm;
            }
            catch (Exception ex) { _logger.LogError("GetPostDetail ex: {m}", ex.Message); return null; }
        }

        // ── Instagram Post Detail ─────────────────────────────────────
        private async Task<PostDetailViewModel?> GetInstagramPostDetailAsync(string postId, string userEmail)
        {
            var accessToken = await GetInstagramTokenAsync(userEmail);
            if (string.IsNullOrEmpty(accessToken)) return null;

            try
            {
                var client = _http.CreateClient();
                var resp = await client.GetAsync(
                    $"https://graph.instagram.com/{postId}"
                    + $"?fields=id,caption,media_url,permalink,timestamp,media_type,thumbnail_url"
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

                if (vm.MediaType == "CAROUSEL_ALBUM")
                {
                    var childResp = await client.GetAsync(
                        $"https://graph.instagram.com/{postId}/children"
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
                    var insResp = await client.GetAsync(
                        $"https://graph.instagram.com/v25.0/{postId}/insights"
                        + $"?metric=likes,comments,reach,shares,saved&access_token={accessToken}");
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
                                    case "likes": vm.LikesCount = val; break;
                                    case "comments": vm.CommentsCount = val; break;
                                    case "reach": vm.Reach = val; break;
                                    case "shares": vm.SharesCount = val; break;
                                    case "saved": vm.SavesCount = val; break;
                                }
                            }
                        }
                    }
                }
                catch { /* optional */ }

                // Comments + replies
                vm.Comments = await GetInstagramCommentsAsync(postId, accessToken);
                return vm;
            }
            catch (Exception ex) { _logger.LogError("GetIGDetail ex: {m}", ex.Message); return null; }
        }

        // ── Instagram Comments with nested replies ────────────────────
        private async Task<List<CommentRowViewModel>> GetInstagramCommentsAsync(string mediaId, string token)
        {
            var list = new List<CommentRowViewModel>();
            var client = _http.CreateClient();
            var resp = await client.GetAsync(
                $"https://graph.instagram.com/v25.0/{mediaId}/comments"
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
        public async Task<bool> LikePostAsync(string postId, string userEmail)
        {
            var userToken = await GetTokenAsync(userEmail);
            if (string.IsNullOrEmpty(userToken)) return false;
            var page = await GetFirstPageAsync(userToken);
            if (page == null) return false;
            try
            {
                var client = _http.CreateClient();
                var resp = await client.PostAsync(
                    $"https://graph.facebook.com/v19.0/{postId}/likes?access_token={page.Value.pageToken}", null);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex) { _logger.LogError("Like ex: {m}", ex.Message); return false; }
        }

        // ════════════════════════════════════════════════════════════
        // 5. ADD COMMENT — Facebook + Instagram
        // ════════════════════════════════════════════════════════════
        public async Task<bool> AddCommentAsync(string postId, string message, string userEmail, string platform = "facebook")
        {
            if (platform.ToLower() == "instagram")
            {
                var igToken = await GetInstagramTokenAsync(userEmail);
                if (string.IsNullOrEmpty(igToken)) return false;
                try
                {
                    var client = _http.CreateClient();
                    // Instagram — query params mein bhejo, body nahi
                    var url = $"https://graph.instagram.com/v19.0/{postId}/comments"
                               + $"?message={Uri.EscapeDataString(message)}"
                               + $"&access_token={igToken}";
                    var resp = await client.PostAsync(url, null);
                    var body2 = await resp.Content.ReadAsStringAsync();
                    _logger.LogInformation("IG Comment response: {b}", body2);
                    return resp.IsSuccessStatusCode;
                }
                catch (Exception ex) { _logger.LogError("IG Comment ex: {m}", ex.Message); return false; }
            }

            // Facebook
            var userToken = await GetTokenAsync(userEmail);
            if (string.IsNullOrEmpty(userToken)) return false;
            var page = await GetFirstPageAsync(userToken);
            if (page == null) return false;
            try
            {
                var client = _http.CreateClient();
                var resp = await client.PostAsync(
                    $"https://graph.facebook.com/v19.0/{postId}/comments"
                    + $"?message={Uri.EscapeDataString(message)}"
                    + $"&access_token={page.Value.pageToken}", null);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex) { _logger.LogError("FB Comment ex: {m}", ex.Message); return false; }
        }

        // ════════════════════════════════════════════════════════════
        // 6. REPLY TO COMMENT — Facebook + Instagram
        // ════════════════════════════════════════════════════════════
        public async Task<bool> ReplyToCommentAsync(string commentId, string message, string userEmail, string platform = "facebook")
        {
            if (platform.ToLower() == "instagram")
            {
                var igToken = await GetInstagramTokenAsync(userEmail);
                if (string.IsNullOrEmpty(igToken)) return false;
                try
                {
                    var client = _http.CreateClient();
                    // Instagram reply — query params mein bhejo
                    var url = $"https://graph.instagram.com/v19.0/{commentId}/replies"
                               + $"?message={Uri.EscapeDataString(message)}"
                               + $"&access_token={igToken}";
                    var resp = await client.PostAsync(url, null);
                    var body2 = await resp.Content.ReadAsStringAsync();
                    _logger.LogInformation("IG Reply response: {b}", body2);
                    return resp.IsSuccessStatusCode;
                }
                catch (Exception ex) { _logger.LogError("IG Reply ex: {m}", ex.Message); return false; }
            }

            // Facebook
            var userToken2 = await GetTokenAsync(userEmail);
            if (string.IsNullOrEmpty(userToken2)) return false;
            var page2 = await GetFirstPageAsync(userToken2);
            if (page2 == null) return false;
            try
            {
                var client = _http.CreateClient();
                var resp = await client.PostAsync(
                    $"https://graph.facebook.com/v19.0/{commentId}/comments"
                    + $"?message={Uri.EscapeDataString(message)}"
                    + $"&access_token={page2.Value.pageToken}", null);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex) { _logger.LogError("FB Reply ex: {m}", ex.Message); return false; }
        }
    }
}