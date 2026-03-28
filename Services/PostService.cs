using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using SocialMediaPanel.ViewModels;

namespace SocialMediaPanel.Services
{
    public interface IPostService
    {
        Task<PostListViewModel> GetPostsAsync(string pageId, string platform = "facebook");
        Task<PostDetailViewModel?> GetPostDetailAsync(string postId, string pageId);
        Task<bool> LikePostAsync(string postId);
        Task<bool> AddCommentAsync(string postId, string message);
    }

    public class PostService : IPostService
    {
        private readonly AppDbContext _db;
        private readonly IHttpClientFactory _http;
        private readonly IConfiguration _config;
        private readonly ILogger<PostService> _logger;

        public PostService(AppDbContext db, IHttpClientFactory http,
                           IConfiguration config, ILogger<PostService> logger)
        {
            _db = db;
            _http = http;
            _config = config;
            _logger = logger;
        }

        // Token from appsettings.json → "Facebook:PageAccessToken"
        private string Token => _config["Facebook:PageAccessToken"] ?? "";

        // ── 1. POSTS LIST ─────────────────────────────────────────────
        public async Task<PostListViewModel> GetPostsAsync(string pageId, string platform = "facebook")
        {
            var insights = await _db.PostInsights
                .Where(p => p.PageId == pageId && p.Platform == platform)
                .OrderByDescending(p => p.UpdatedAt)
                .ToListAsync();

            var rows = new List<PostRowViewModel>();

            foreach (var ins in insights)
            {
                var row = new PostRowViewModel
                {
                    Id = ins.Id,
                    PostId = ins.PostId,
                    PageId = ins.PageId,
                    Platform = ins.Platform,
                    LikesCount = ins.LikesCount,
                    CommentsCount = ins.CommentsCount,
                    SharesCount = ins.SharesCount,
                    Reach = ins.Reach,
                    Impressions = ins.Impressions,
                    SavesCount = ins.SavesCount,
                    UpdatedAt = ins.UpdatedAt,
                };

                // Graph API se message, image, date fetch karo
                var fb = await FetchGraphPostAsync(ins.PostId);
                if (fb != null)
                {
                    row.Message = fb.Message;
                    row.FullPicture = fb.FullPicture;
                    row.CreatedTime = fb.CreatedTime;
                    row.PermalinkUrl = fb.PermalinkUrl;
                }

                rows.Add(row);
            }

            return new PostListViewModel
            {
                Posts = rows,
                ActivePlatform = platform,
                ActiveTab = "published"
            };
        }

        // ── 2. POST DETAIL ────────────────────────────────────────────
        public async Task<PostDetailViewModel?> GetPostDetailAsync(string postId, string pageId)
        {
            var ins = await _db.PostInsights
                .FirstOrDefaultAsync(p => p.PostId == postId && p.PageId == pageId);

            if (ins == null) return null;

            var vm = new PostDetailViewModel
            {
                Id = ins.Id,
                PostId = ins.PostId,
                PageId = ins.PageId,
                Platform = ins.Platform,
                LikesCount = ins.LikesCount,
                CommentsCount = ins.CommentsCount,
                SharesCount = ins.SharesCount,
                Reach = ins.Reach,
                Impressions = ins.Impressions,
                SavesCount = ins.SavesCount,
                UpdatedAt = ins.UpdatedAt,
            };

            // Graph API se post content
            var fb = await FetchGraphPostAsync(postId);
            if (fb != null)
            {
                vm.Message = fb.Message;
                vm.FullPicture = fb.FullPicture;
                vm.CreatedTime = fb.CreatedTime;
                vm.PermalinkUrl = fb.PermalinkUrl;
            }

            // Comments from page_comments table
            try
            {
                vm.Comments = await _db.PageComments
                    .Where(c => c.PostId == postId && c.PageId == pageId)
                    .OrderBy(c => c.CommentTime)
                    .Select(c => new CommentRowViewModel
                    {
                        SenderName = c.SenderName ?? "Unknown",
                        Message = c.Message,
                        CommentTime = c.CommentTime
                    })
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("PageComments load failed: {msg}", ex.Message);
                vm.Comments = new List<CommentRowViewModel>();
            }

            return vm;
        }

        // ── 3. LIKE POST (Facebook Graph API) ────────────────────────
        public async Task<bool> LikePostAsync(string postId)
        {
            if (string.IsNullOrEmpty(Token)) return false;
            try
            {
                var client = _http.CreateClient();
                var url = $"https://graph.facebook.com/v19.0/{postId}/likes"
                           + $"?access_token={Token}";
                var resp = await client.PostAsync(url, null);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError("LikePost failed: {msg}", ex.Message);
                return false;
            }
        }

        // ── 4. ADD COMMENT (Facebook Graph API) ──────────────────────
        public async Task<bool> AddCommentAsync(string postId, string message)
        {
            if (string.IsNullOrEmpty(Token) || string.IsNullOrEmpty(message)) return false;
            try
            {
                var client = _http.CreateClient();
                var url = $"https://graph.facebook.com/v19.0/{postId}/comments"
                            + $"?message={Uri.EscapeDataString(message)}"
                            + $"&access_token={Token}";
                var resp = await client.PostAsync(url, null);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError("AddComment failed: {msg}", ex.Message);
                return false;
            }
        }

        // ── 5. FACEBOOK GRAPH API FETCH ───────────────────────────────
        private async Task<FbPostData?> FetchGraphPostAsync(string postId)
        {
            if (string.IsNullOrEmpty(Token))
            {
                _logger.LogWarning("Facebook:PageAccessToken is missing in appsettings.json");
                return null;
            }

            try
            {
                var client = _http.CreateClient();
                var url = $"https://graph.facebook.com/v19.0/{postId}"
                           + $"?fields=message,full_picture,created_time,permalink_url,attachments"
                           + $"&access_token={Token}";

                var resp = await client.GetAsync(url);

                if (!resp.IsSuccessStatusCode)
                {
                    var errBody = await resp.Content.ReadAsStringAsync();
                    _logger.LogWarning("Graph API error for {postId}: {status} — {body}",
                        postId, resp.StatusCode, errBody);
                    return null;
                }

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var d = new FbPostData();

                if (root.TryGetProperty("message", out var m)) d.Message = m.GetString();
                if (root.TryGetProperty("full_picture", out var p)) d.FullPicture = p.GetString();
                if (root.TryGetProperty("permalink_url", out var pl)) d.PermalinkUrl = pl.GetString();
                if (root.TryGetProperty("created_time", out var ct) &&
                    DateTime.TryParse(ct.GetString(), out var dt)) d.CreatedTime = dt;

                // Agar full_picture nahi mila toh attachments se try karo
                if (string.IsNullOrEmpty(d.FullPicture) &&
                    root.TryGetProperty("attachments", out var att) &&
                    att.TryGetProperty("data", out var attData) &&
                    attData.GetArrayLength() > 0)
                {
                    var first = attData[0];
                    if (first.TryGetProperty("media", out var media) &&
                        media.TryGetProperty("image", out var img) &&
                        img.TryGetProperty("src", out var src))
                    {
                        d.FullPicture = src.GetString();
                    }
                }

                return d;
            }
            catch (Exception ex)
            {
                _logger.LogError("FetchGraphPost exception for {postId}: {msg}", postId, ex.Message);
                return null;
            }
        }

        private class FbPostData
        {
            public string? Message { get; set; }
            public string? FullPicture { get; set; }
            public string? PermalinkUrl { get; set; }
            public DateTime? CreatedTime { get; set; }
        }
    }
}