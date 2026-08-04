using Microsoft.AspNetCore.Mvc;
using SocialMediaPanel.Services;
using SocialMediaPanel.ViewModels;

namespace SocialMediaPanel.Controllers
{
    public class PostsController : Controller
    {
        private readonly IPostService _postService;
        private readonly AuditLogService _audit;

        public PostsController(IPostService postService, AuditLogService audit)
        {
            _postService = postService;
            _audit = audit;
        }

        private int? GetUserId() => HttpContext.Session.GetInt32("UserId");
        private string GetUserEmail() => HttpContext.Session.GetString("UserEmail") ?? string.Empty;

        // ── CSV EXPORT — Posts (Category A, phase 4) ──────────────────────
        [HttpGet]
        public async Task<IActionResult> Export(string platform = "facebook")
        {
            if (GetUserId() == null) return Unauthorized();
            var email = GetUserEmail();
            if (string.IsNullOrEmpty(email)) return Unauthorized();

            var vm = platform == "instagram"
                ? await _postService.GetInstagramPostsAsync(GetUserId()!.Value, email, null)
                : await _postService.GetPostsAsync(GetUserId()!.Value, email, null, platform);

            var csv = CsvExportHelper.BuildCsv(
                new[] { "PostId", "Platform", "Message", "CreatedTime", "Likes", "Comments", "Shares", "TotalEngagement", "PermalinkUrl" },
                vm.Posts.Select(p => new object?[]
                {
                    p.PostId, p.Platform, p.Message, p.CreatedTime?.ToString("yyyy-MM-dd HH:mm:ss"),
                    p.LikesCount, p.CommentsCount, p.SharesCount, p.TotalEngagement, p.PermalinkUrl
                }));

            return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"posts-{platform}-{DateTime.UtcNow:yyyyMMdd}.csv");
        }

        // ── GET /Posts ────────────────────────────────────────────────
        public async Task<IActionResult> Index(string platform = "facebook", string tab = "published", string? pageId = null)
        {
            if (GetUserId() == null) return RedirectToAction("Login", "Account");

            var email = GetUserEmail();
            var userId = GetUserId()!.Value;
            ViewBag.UserName = HttpContext.Session.GetString("UserName") ?? "User";
            ViewBag.CompanyName = HttpContext.Session.GetString("CompanyName") ?? "Your Workspace";

            // ── DRAFT TAB — DB se draft posts ──────────────────────
            if (tab == "draft")
            {
                var drafts = await _postService.GetDraftPostsAsync(userId, platform);
                drafts.ActiveTab = "draft";
                drafts.ActivePlatform = platform;
                return View(drafts);
            }

            // ── PUBLISHED — API se posts ───────────────────────────
            if (string.IsNullOrEmpty(email))
            {
                ViewBag.NoToken = true;
                return View(new PostListViewModel { ActivePlatform = platform, ActiveTab = tab });
            }

            PostListViewModel vm;
            if (platform == "instagram")
                vm = await _postService.GetInstagramPostsAsync(userId, email, pageId);
            else
                vm = await _postService.GetPostsAsync(userId, email, pageId, platform);

            vm.ActiveTab = tab;
            return View(vm);
        }

        // ── GET /Posts/Details → modal partial ───────────────────────
        [HttpGet]
        public async Task<IActionResult> Details(string postId, string platform = "facebook", string? pageId = null)
        {
            if (GetUserId() == null) return Unauthorized();
            if (string.IsNullOrEmpty(postId)) return BadRequest("postId required");

            var email = GetUserEmail();
            if (string.IsNullOrEmpty(email)) return Unauthorized();

            var post = await _postService.GetPostDetailAsync(postId, GetUserId()!.Value, email, pageId, platform);
            if (post == null) return NotFound();

            return PartialView("_PostModal", post);
        }

        // ── GET /Posts/GetDraft ───────────────────────────────────────
        // Draft data service se uthao — _context yahan nahi hai
        [HttpGet]
        public async Task<IActionResult> GetDraft(string draftId)
        {
            var userId = GetUserId();
            if (userId == null) return Unauthorized();

            var draft = await _postService.GetDraftByIdAsync(draftId, userId.Value);
            if (draft == null)
                return Json(new { success = false, message = "Draft not found" });
            var imagesList = !string.IsNullOrEmpty(draft.media_url)
                     ? draft.media_url.Split(',').ToList()
                     : new List<string>();
            return Json(new
            {
                success = true,
                message = draft.Message,
                platform = draft.Platform,
                id = draft.PostId,
                draftMediaUrls = imagesList // Frontend ko list mil jayegi
            });
        }

        // ── POST /Posts/Like ──────────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> Like(string postId, string? pageId = null)
        {
            if (GetUserId() == null)
                return Json(new { success = false, message = "Not logged in" });

            var ok = await _postService.LikePostAsync(postId, GetUserId()!.Value, GetUserEmail(), pageId);
            return Json(new { success = ok, message = ok ? "Liked!" : "Failed. Token expired?" });
        }

        // ── POST /Posts/Comment ───────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> Comment(string postId, string message, string platform = "facebook", string? pageId = null)
        {
            if (GetUserId() == null)
                return Json(new { success = false, message = "Not logged in" });

            if (string.IsNullOrEmpty(message))
                return Json(new { success = false, message = "Comment empty" });

            var ok = await _postService.AddCommentAsync(postId, message, GetUserId()!.Value, GetUserEmail(), pageId, platform);
            return Json(new { success = ok, message = ok ? "Comment posted!" : "Failed." });
        }

        // ── POST /Posts/Reply ─────────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> Reply(string commentId, string message, string platform = "facebook", string? pageId = null)
        {
            if (GetUserId() == null)
                return Json(new { success = false, message = "Not logged in" });

            if (string.IsNullOrEmpty(message))
                return Json(new { success = false, message = "Reply empty" });

            var ok = await _postService.ReplyToCommentAsync(commentId, message, GetUserId()!.Value, GetUserEmail(), pageId, platform);
            return Json(new { success = ok, message = ok ? "Reply posted!" : "Failed." });
        }

        // ── POST /Posts/DeleteComment ─────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> DeleteComment(string commentId, string platform = "facebook", string? pageId = null)
        {
            if (GetUserId() == null)
                return Json(new { success = false, message = "Not logged in" });

            var ok = await _postService.DeleteCommentAsync(commentId, GetUserId()!.Value, pageId, platform);
            if (ok) _audit.Log(GetUserId(), "comment.delete", $"commentId={commentId} platform={platform}");
            return Json(new { success = ok, message = ok ? "Comment deleted." : "Failed to delete comment." });
        }

        // ── POST /Posts/HideComment ────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> HideComment(string commentId, bool hide = true, string platform = "facebook", string? pageId = null)
        {
            if (GetUserId() == null)
                return Json(new { success = false, message = "Not logged in" });

            var ok = await _postService.HideCommentAsync(commentId, hide, GetUserId()!.Value, pageId, platform);
            if (ok) _audit.Log(GetUserId(), hide ? "comment.hide" : "comment.unhide", $"commentId={commentId} platform={platform}");
            return Json(new { success = ok, message = ok ? (hide ? "Comment hidden." : "Comment unhidden.") : "Failed." });
        }
    }
}