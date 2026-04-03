using Microsoft.AspNetCore.Mvc;
using SocialMediaPanel.Services;
using SocialMediaPanel.ViewModels;

namespace SocialMediaPanel.Controllers
{
    public class PostsController : Controller
    {
        private readonly IPostService _postService;

        public PostsController(IPostService postService)
        {
            _postService = postService;
        }

        private int? GetUserId() => HttpContext.Session.GetInt32("UserId");
        private string GetUserEmail() => HttpContext.Session.GetString("UserEmail") ?? string.Empty;

        // ── GET /Posts ────────────────────────────────────────────────
        public async Task<IActionResult> Index(string platform = "facebook", string tab = "published")
        {
            if (GetUserId() == null) return RedirectToAction("Login", "Account");

            var email = GetUserEmail();
            ViewBag.UserName = HttpContext.Session.GetString("UserName") ?? "User";
            ViewBag.CompanyName = HttpContext.Session.GetString("CompanyName") ?? "Your Workspace";

            // ── DRAFT TAB — DB se draft posts ──────────────────────
            if (tab == "draft")
            {
                var userId = GetUserId()!.Value;
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
                vm = await _postService.GetInstagramPostsAsync(email);
            else
                vm = await _postService.GetPostsAsync(email, platform);

            vm.ActiveTab = tab;
            return View(vm);
        }

        // ── GET /Posts/Details → modal partial ───────────────────────
        [HttpGet]
        public async Task<IActionResult> Details(string postId, string platform = "facebook")
        {
            if (GetUserId() == null) return Unauthorized();
            if (string.IsNullOrEmpty(postId)) return BadRequest("postId required");

            var email = GetUserEmail();
            if (string.IsNullOrEmpty(email)) return Unauthorized();

            var post = await _postService.GetPostDetailAsync(postId, email, platform);
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
        public async Task<IActionResult> Like(string postId)
        {
            if (GetUserId() == null)
                return Json(new { success = false, message = "Not logged in" });

            var ok = await _postService.LikePostAsync(postId, GetUserEmail());
            return Json(new { success = ok, message = ok ? "Liked!" : "Failed. Token expired?" });
        }

        // ── POST /Posts/Comment ───────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> Comment(string postId, string message, string platform = "facebook")
        {
            if (GetUserId() == null)
                return Json(new { success = false, message = "Not logged in" });

            if (string.IsNullOrEmpty(message))
                return Json(new { success = false, message = "Comment empty" });

            var ok = await _postService.AddCommentAsync(postId, message, GetUserEmail(), platform);
            return Json(new { success = ok, message = ok ? "Comment posted!" : "Failed." });
        }

        // ── POST /Posts/Reply ─────────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> Reply(string commentId, string message, string platform = "facebook")
        {
            if (GetUserId() == null)
                return Json(new { success = false, message = "Not logged in" });

            if (string.IsNullOrEmpty(message))
                return Json(new { success = false, message = "Reply empty" });

            var ok = await _postService.ReplyToCommentAsync(commentId, message, GetUserEmail(), platform);
            return Json(new { success = ok, message = ok ? "Reply posted!" : "Failed." });
        }
    }
}