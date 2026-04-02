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

            if (string.IsNullOrEmpty(email))
            {
                ViewBag.NoToken = true;
                return View(new SocialMediaPanel.ViewModels.PostListViewModel
                {
                    ActivePlatform = platform,
                    ActiveTab = tab
                });
            }


            PostListViewModel vm;

            if (platform == "instagram")
                vm = await _postService.GetInstagramPostsAsync(email);
            else
                vm = await _postService.GetPostsAsync(email, platform);

            //var vm = await _postService.GetPostsAsync(email, platform);
            vm.ActiveTab = tab;
            return View(vm);
        }

        // ── GET /Posts/Details → modal partial ───────────────────────
        [HttpGet]
        public async Task<IActionResult> Details(string postId, string platform)
        {
            if (GetUserId() == null) return Unauthorized();
            if (string.IsNullOrEmpty(postId)) return BadRequest("postId required");

            var email = GetUserEmail();
            if (string.IsNullOrEmpty(email)) return Unauthorized();

            var post = await _postService.GetPostDetailAsync(postId, email,platform);
            if (post == null) return NotFound();

            return PartialView("_PostModal", post);
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
        public async Task<IActionResult> Comment(string postId, string message)
        {
            if (GetUserId() == null)
                return Json(new { success = false, message = "Not logged in" });

            if (string.IsNullOrEmpty(message))
                return Json(new { success = false, message = "Comment empty" });

            var ok = await _postService.AddCommentAsync(postId, message, GetUserEmail());
            return Json(new { success = ok, message = ok ? "Comment posted!" : "Failed." });
        }

        // ── POST /Posts/Reply → comment ka reply ─────────────────────
        [HttpPost]
        public async Task<IActionResult> Reply(string commentId, string message)
        {
            if (GetUserId() == null)
                return Json(new { success = false, message = "Not logged in" });

            if (string.IsNullOrEmpty(message))
                return Json(new { success = false, message = "Reply empty" });

            var ok = await _postService.ReplyToCommentAsync(commentId, message, GetUserEmail());
            return Json(new { success = ok, message = ok ? "Reply posted!" : "Failed." });
        }
    }
}