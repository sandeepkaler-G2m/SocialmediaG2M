using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Services;

namespace SocialMediaPanel.Controllers
{
    public class PostsController : Controller
    {
        private readonly IPostService _postService;
        private readonly AppDbContext _db;

        public PostsController(IPostService postService, AppDbContext db)
        {
            _postService = postService;
            _db = db;
        }

        // ── Session se userId lo ─────────────────────────────────────
        private int? GetUserId() => HttpContext.Session.GetInt32("UserId");

        // ── PageId: session mein hai toh wahi, warna post_insights se pehla ──
        private async Task<string?> GetPageIdAsync()
        {
            // Session mein stored pageId (Facebook connect ke waqt save hoti hai)
            var sessionPageId = HttpContext.Session.GetString("PageId");
            if (!string.IsNullOrEmpty(sessionPageId))
                return sessionPageId;

            // Fallback: post_insights table se pehla pageId uthao
            var pageId = await _db.PostInsights
                .Where(p => p.PageId != null)
                .Select(p => p.PageId)
                .FirstOrDefaultAsync();

            return pageId;
        }

        // ── GET /Posts?platform=facebook&tab=published ────────────────
        public async Task<IActionResult> Index(string platform = "facebook", string tab = "published")
        {
            var userId = GetUserId();
            if (userId == null) return RedirectToAction("Login", "Account");

            ViewBag.UserName = HttpContext.Session.GetString("UserName") ?? "User";
            ViewBag.CompanyName = HttpContext.Session.GetString("CompanyName") ?? "Your Workspace";

            // PageId — session ya DB se
            var pageId = await GetPageIdAsync();

            if (string.IsNullOrEmpty(pageId))
            {
                // Koi page connected nahi — empty list dikhao
                var empty = new SocialMediaPanel.ViewModels.PostListViewModel
                {
                    ActivePlatform = platform,
                    ActiveTab = tab
                };
                ViewBag.NoPageConnected = true;
                return View(empty);
            }

            var vm = await _postService.GetPostsAsync(pageId, platform);
            vm.ActiveTab = tab;

            return View(vm);
        }

        // ── GET /Posts/Details?postId=xxx&pageId=yyy → modal partial ─
        // postId aur pageId directly post_insights table ke fields hain
        [HttpGet]
        public async Task<IActionResult> Details(string postId, string pageId)
        {
            // pageId agar empty ho toh session/DB se lo
            if (string.IsNullOrEmpty(pageId))
                pageId = await GetPageIdAsync() ?? string.Empty;

            if (string.IsNullOrEmpty(postId))
                return BadRequest("postId is required");

            // Pehle verify karo ki yeh post exist karti hai post_insights mein
            var exists = await _db.PostInsights
                .AnyAsync(p => p.PostId == postId && p.PageId == pageId);

            if (!exists) return NotFound();

            var post = await _postService.GetPostDetailAsync(postId, pageId);
            if (post == null) return NotFound();

            return PartialView("_PostModal", post);
        }
    }
}