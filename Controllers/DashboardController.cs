using Azure.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using SocialMediaPanel.Services;
using SocialMediaPanel.ViewModels;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SocialMediaPanel.Controllers
{
    public class DashboardController : Controller
    {
        private readonly AppDbContext _context;
        private readonly PostService _postService;

        public DashboardController(AppDbContext context,PostService postService)
        {
            _context = context;
            _postService = postService;
        }

        // ==================== DASHBOARD INDEX ====================
        public async Task<IActionResult> Index()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return RedirectToAction("Login", "Account");

            var user = await _context.Users
                .Where(u => u.Id == userId)
                .FirstOrDefaultAsync();

            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Login", "Account");
            }

            ViewBag.UserName = user.Name;
            ViewBag.UserEmail = user.Email;
            ViewBag.CompanyName = user.CompanyName ?? "Your Workspace";

            var allPosts = await GetAllPosts();

            var posts = allPosts
                .Where(x => x.created_at >= DateTime.Now.AddDays(-7)) // last 7 days
                .OrderByDescending(x => x.created_at)
                .ToList();

            return View(posts);
        }

        // ==================== COMPOSE POST ====================
        // Dashboard "Compose Post" button → Dashboard/Compose view
        public async Task<IActionResult> Compose()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return RedirectToAction("Login", "Account");

            var user = await _context.Users
                .Where(u => u.Id == userId)
                .FirstOrDefaultAsync();

            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Login", "Account");
            }

            ViewBag.UserName = user.Name;
            ViewBag.UserEmail = user.Email;
            ViewBag.CompanyName = user.CompanyName ?? "Your Workspace";

            return View(); // Views/Dashboard/Compose.cshtml load hoga
        }


        public async Task<List<SocialPost>> GetAllPosts()
        {
            var username = HttpContext.Session.GetString("UserEmail");
            var fbtoken = await _postService.GetTokenAsync(username);
            var page = await _postService.GetFirstPageAsync(fbtoken.ToString());
            var (pageId, pageToken) = page.Value;

            List<SocialPost> allPosts = new List<SocialPost>();
            var httpClient = new HttpClient(); // Best practice: reuse one client

            // 1. ✅ Get Facebook Page Name
            string fbPageInfoUrl = $"https://graph.facebook.com/v19.0/{pageId}?fields=name&access_token={pageToken}";
            var fbPageResponse = await httpClient.GetStringAsync(fbPageInfoUrl);
            dynamic fbPageData = Newtonsoft.Json.JsonConvert.DeserializeObject(fbPageResponse);
            string fbPageName = fbPageData.name;

            // Fetch Facebook Posts
            string fbUrl = $"https://graph.facebook.com/v19.0/{pageId}/posts"
                             + $"?fields=message,full_picture,created_time,"
                             + $"likes.summary(true),comments.summary(true),shares"
                             + $"&access_token={pageToken}";
            var fbResponse = await httpClient.GetStringAsync(fbUrl);
            dynamic fbData = Newtonsoft.Json.JsonConvert.DeserializeObject(fbResponse);

            foreach (var item in fbData.data)
            {
                allPosts.Add(new SocialPost
                {
                    platform = "facebook",
                    account_name = fbPageName, // Added this
                    message = item.message,
                    media_url = item.full_picture,
                    created_at = item.created_time,
                    like_count = item.likes?.summary?.total_count ?? 0,
                    comment_count = item.comments?.summary?.total_count ?? 0,
                    share_count = item.shares?.count ?? 0
                });
            }

            // 2. ✅ Get Instagram Name
            var instatoken = await _postService.GetInstagramTokenAsync(username);

            // Note: Use "username" for IG handle or "name" if available in your API scope
            string igInfoUrl = $"https://graph.instagram.com/me?fields=username&access_token={instatoken}";
            var igInfoResponse = await httpClient.GetStringAsync(igInfoUrl);
            dynamic igInfoData = Newtonsoft.Json.JsonConvert.DeserializeObject(igInfoResponse);
            string igAccountName = igInfoData.username;

            // Fetch Instagram Posts
            string igUrl = $"https://graph.instagram.com/me/media?fields=caption,media_url,timestamp,like_count,comments_count&access_token={instatoken}"; var igResponse = await httpClient.GetStringAsync(igUrl);
            dynamic igData = Newtonsoft.Json.JsonConvert.DeserializeObject(igResponse);

            foreach (var item in igData.data)
            {
                allPosts.Add(new SocialPost
                {
                    platform = "instagram",
                    account_name = igAccountName, // Added this
                    message = item.caption,
                    media_url = item.media_url,
                    created_at = item.timestamp,
                    like_count = item.like_count ?? 0,
                    comment_count = item.comments_count ?? 0
                });
            }

            return allPosts.OrderByDescending(x => x.created_at).ToList();
        }

        [HttpGet]
        public async Task<IActionResult> Leads()
        {
            var leads = await _context.Leads
                .OrderByDescending(l => l.CreatedAt)
                .Select(l => new LeadViewModel
                {
                    Id = l.Id,
                    LeadId = l.LeadId,
                    PageId = l.PageId,
                    FormId = l.FormId,
                    FullName = l.FullName,
                    Email = l.Email,
                    Phone = l.Phone,
                    Platform = l.Platform,
                    RawData = l.RawData,
                    Status = l.Status ?? "open",
                    CreatedAt = l.CreatedAt
                })
                .ToListAsync();

            return View(leads);
        }

        [HttpGet]
        [Route("Leads/GetAll")]
        public async Task<IActionResult> GetAll(string? status)
        {
            var query = _context.Leads.AsQueryable();

            if (!string.IsNullOrEmpty(status) && status != "all")
               query = query.Where(l => l.Status == status);

            var leads = await query
                .OrderByDescending(l => l.CreatedAt)
                .Select(l => new
                {
                    id = l.Id,
                    leadId = l.LeadId,
                    name = l.FullName ?? "(No name)",
                    email = l.Email ?? "",
                    phone = l.Phone ?? "",
                    platform = l.Platform ?? "",
                    formId = l.FormId ?? "",
                    pageId = l.PageId ?? "",
                    rawData = l.RawData ?? "",
                    status = (l.Status ?? "open").ToLower(),
                    createdAt = l.CreatedAt.ToString("MMM dd, yyyy h:mm tt")
                })
                .ToListAsync();

            return Json(leads);
        }

        // ── UpdateStatus — AJAX status change ───────────────────────
        [HttpPost]
        [Route("Leads/UpdateStatus")]
        public async Task<IActionResult> UpdateStatus([FromBody] UpdateStatusRequest request)
        {
            var lead = await _context.Leads.FindAsync(request.Id);
            if (lead == null)
                return Json(new { success = false, message = "Lead not found." });

            lead.Status = request.Status;
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // ── Delete — AJAX delete ─────────────────────────────────────
        [HttpPost]
        [Route("Leads/Delete/{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var lead = await _context.Leads.FindAsync(id);
            if (lead == null)
                return Json(new { success = false, message = "Lead not found." });

            _context.Leads.Remove(lead);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // ── Details — optional full detail page ─────────────────────
        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var lead = await _context.Leads.FindAsync(id);
            if (lead == null) return NotFound();
            return Json(lead);
        }

        [HttpPost]
        public async Task<IActionResult> PublishPost(IFormFile image, string message)
        {
            var userId = HttpContext.Session.GetInt32("UserId");

            if (userId == null)
                return Json(new { success = false, message = "User not logged in" });

            // ✅ Get page from DB (you can filter by selected page later)
            var page = await _context.FacebookPages
                .Where(x => x.user_id == userId)
                .OrderByDescending(x => x.id)
                .FirstOrDefaultAsync();

            if (page == null)
                return Json(new { success = false, message = "No Facebook page connected" });

            string pageid = page.page_id;
            string pageaccess = page.page_access_token;

            using var client = new HttpClient();

            // ✅ IMAGE POST
            if (image != null)
            {
                var content = new MultipartFormDataContent();
                content.Add(new StringContent(message ?? ""), "caption");

                var streamContent = new StreamContent(image.OpenReadStream());
                streamContent.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(image.ContentType);

                content.Add(streamContent, "source", image.FileName);

                var response = await client.PostAsync(
                    $"https://graph.facebook.com/v19.0/{pageid}/photos?access_token={pageaccess}",
                    content
                );

                var respText = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return Json(new { success = false, message = respText });

                // ✅ Parse response
                var json = JsonDocument.Parse(respText);
                var postId = json.RootElement.GetProperty("id").GetString();

                // ✅ Save in DB
                var newPost = new SocialPost
                {
                    user_id = userId,
                    page_id = pageid,
                    post_id = postId,
                    message = message,
                    media_url = "", // optional (you can store image path if you save it)
                    platform = "facebook",
                    created_at = DateTime.UtcNow
                };

                _context.SocialPosts.Add(newPost);
                var res = await _context.SaveChangesAsync();

                if (!response.IsSuccessStatusCode)
                    return Json(new { success = false, message = respText });
            }
            else
            {
                // ✅ TEXT POST
                var data = new FormUrlEncodedContent(new[]
                {
            new KeyValuePair<string, string>("message", message ?? ""),
            new KeyValuePair<string, string>("access_token", pageaccess)
        });

                var response = await client.PostAsync(
                    $"https://graph.facebook.com/v19.0/{pageid}/feed",
                    data
                );

                var respText = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return Json(new { success = false, message = respText });

                // ✅ Parse response
                var json = JsonDocument.Parse(respText);
                var postId = json.RootElement.GetProperty("id").GetString();

                // ✅ Save in DB
                var newPost = new SocialPost
                {
                    user_id = userId,
                    page_id = pageid,
                    post_id = postId,
                    message = message,
                    media_url = "", // optional (you can store image path if you save it)
                    platform = "facebook",
                    created_at = DateTime.UtcNow
                };

                _context.SocialPosts.Add(newPost);
                var res = await _context.SaveChangesAsync();


                if (!response.IsSuccessStatusCode)
                    return Json(new { success = false, message = respText });
            }

            return Json(new { success = true, message = "Post published successfully!" });
        }

    }

    public class SocialPost
    {
        public int id { get; set; }
        public int? user_id { get; set; }
        public string page_id { get; set; }
        public string account_name { get; set; }
        public string post_id { get; set; }
        public string message { get; set; }
        public string? media_url { get; set; }
        public string platform { get; set; }
        public DateTime created_at { get; set; }

        public int like_count { get; set; }
        public int comment_count { get; set; }
        public int share_count { get; set; }
    }

}
