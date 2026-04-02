using Google.Apis.Gmail.v1.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.ViewModels;
using System.Text.Json;

namespace SocialMediaPanel.Controllers
{
    public class DashboardController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;

        public DashboardController(AppDbContext context, IConfiguration config, IWebHostEnvironment env)
        {
            _context = context;
            _config = config;
            _env = env;
        }

        // ── INDEX ─────────────────────────────────────────────────────
        public async Task<IActionResult> Index()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var user = await _context.Users.Where(u => u.Id == userId).FirstOrDefaultAsync();
            if (user == null) { HttpContext.Session.Clear(); return RedirectToAction("Login", "Account"); }

            ViewBag.UserName = user.Name;
            ViewBag.UserEmail = user.Email;
            ViewBag.CompanyName = user.CompanyName ?? "Your Workspace";
            return View();
        }

        // ── COMPOSE ───────────────────────────────────────────────────
        public async Task<IActionResult> Compose()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return RedirectToAction("Login", "Account");

            var user = await _context.Users.Where(u => u.Id == userId).FirstOrDefaultAsync();
            if (user == null) { HttpContext.Session.Clear(); return RedirectToAction("Login", "Account"); }

            ViewBag.UserName = user.Name;
            ViewBag.UserEmail = user.Email;
            ViewBag.CompanyName = user.CompanyName ?? "Your Workspace";
            return View();
        }

        // ── LEADS ─────────────────────────────────────────────────────
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
                }).ToListAsync();
            return View(leads);
        }

        [HttpGet]
        [Route("Leads/GetAll")]
        public async Task<IActionResult> GetAll(string? status)
        {
            var query = _context.Leads.AsQueryable();
            if (!string.IsNullOrEmpty(status) && status != "all")
                query = query.Where(l => l.Status == status);

            var leads = await query.OrderByDescending(l => l.CreatedAt).Select(l => new {
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
            }).ToListAsync();
            return Json(leads);
        }

        [HttpPost]
        [Route("Leads/UpdateStatus")]
        public async Task<IActionResult> UpdateStatus([FromBody] UpdateStatusRequest request)
        {
            var lead = await _context.Leads.FindAsync(request.Id);
            if (lead == null) return Json(new { success = false, message = "Lead not found." });
            lead.Status = request.Status;
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        [Route("Leads/Delete/{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var lead = await _context.Leads.FindAsync(id);
            if (lead == null) return Json(new { success = false, message = "Lead not found." });
            _context.Leads.Remove(lead);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var lead = await _context.Leads.FindAsync(id);
            if (lead == null) return NotFound();
            return Json(lead);
        }


        // ════════════════════════════════════════════════════════════════
        // PUBLISH POST
        //
        // Facebook:
        //   → FacebookPages table se page_access_token uthao
        //   → Binary upload — koi URL nahi chahiye
        //
        // Instagram:
        //   → UserTokens table → username=email → instagramtoken
        //   → ImgBB se public URL banao (localhost + production dono pe kaam karta hai)
        //   → appsettings.json mein: "ImgBB": { "ApiKey": "your_key" }
        // ════════════════════════════════════════════════════════════════
        [HttpPost]
        public async Task<IActionResult> PublishPost(
            List<IFormFile> images, string message, string platform, string campaign, bool isDraft = false)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return Json(new { success = false, message = "User not logged in" });

            var userEmail = HttpContext.Session.GetString("UserEmail") ?? "";
            using var http = new HttpClient();

            if (isDraft)
            {
                _context.SocialPosts.Add(new SocialPost
                {
                    user_id = userId,
                    page_id = "",
                    post_id = "",
                    message = message,
                    media_url = "",
                    platform = platform,
                    status = "draft",   // 👈 IMPORTANT
                    created_at = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                return Json(new { success = true, message = "Draft saved" });
            }
            // ─────────────────────────────────────────────────────────────
            // FACEBOOK
            // ─────────────────────────────────────────────────────────────
            if (platform?.ToLower() == "fb" || platform?.ToLower() == "facebook")
            {
                var page = await _context.FacebookPages
                    .Where(x => x.user_id == userId)
                    .OrderByDescending(x => x.id)
                    .FirstOrDefaultAsync();

                if (page == null)
                    return Json(new { success = false, message = "No Facebook page connected." });

                string pageId = page.page_id;
                string pageToken = page.page_access_token;
                string? fbPostId = null;

                if (images != null && images.Count == 1)
                {
                    // Single image
                    var mc = new MultipartFormDataContent();
                    var sc = new StreamContent(images[0].OpenReadStream());
                    sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(images[0].ContentType);
                    mc.Add(sc, "source", images[0].FileName);
                    mc.Add(new StringContent(message ?? ""), "caption");

                    var r = await http.PostAsync(
                        $"https://graph.facebook.com/v19.0/{pageId}/photos?access_token={pageToken}", mc);
                    var b = await r.Content.ReadAsStringAsync();
                    if (!r.IsSuccessStatusCode)
                        return Json(new { success = false, message = "FB upload failed: " + b });

                    fbPostId = JsonDocument.Parse(b).RootElement.GetProperty("id").GetString();
                }
                else if (images != null && images.Count > 1)
                {
                    // Multiple images → unpublished photos → one feed post
                    var photoIds = new List<string>();
                    foreach (var img in images.Take(10))
                    {
                        var mc = new MultipartFormDataContent();
                        var sc = new StreamContent(img.OpenReadStream());
                        sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(img.ContentType);
                        mc.Add(sc, "source", img.FileName);
                        mc.Add(new StringContent("false"), "published");

                        var r = await http.PostAsync(
                            $"https://graph.facebook.com/v19.0/{pageId}/photos?access_token={pageToken}", mc);
                        var b = await r.Content.ReadAsStringAsync();
                        if (!r.IsSuccessStatusCode)
                            return Json(new { success = false, message = "FB image upload failed: " + b });

                        photoIds.Add(JsonDocument.Parse(b).RootElement.GetProperty("id").GetString() ?? "");
                    }

                    // One feed post with all photos attached
                    var feed = new MultipartFormDataContent();
                    feed.Add(new StringContent(message ?? ""), "message");
                    feed.Add(new StringContent(pageToken), "access_token");
                    for (int i = 0; i < photoIds.Count; i++)
                        feed.Add(new StringContent($"{{\"media_fbid\":\"{photoIds[i]}\"}}"), $"attached_media[{i}]");

                    var fr = await http.PostAsync($"https://graph.facebook.com/v19.0/{pageId}/feed", feed);
                    var fb2 = await fr.Content.ReadAsStringAsync();
                    if (!fr.IsSuccessStatusCode)
                        return Json(new { success = false, message = "FB multi-image post failed: " + fb2 });

                    fbPostId = JsonDocument.Parse(fb2).RootElement.GetProperty("id").GetString();
                }
                else
                {
                    // Text only
                    var fd = new FormUrlEncodedContent(new[] {
                        new KeyValuePair<string,string>("message",      message ?? ""),
                        new KeyValuePair<string,string>("access_token", pageToken)
                    });
                    var r = await http.PostAsync($"https://graph.facebook.com/v19.0/{pageId}/feed", fd);
                    var b = await r.Content.ReadAsStringAsync();
                    if (!r.IsSuccessStatusCode)
                        return Json(new { success = false, message = "FB post failed: " + b });

                    fbPostId = JsonDocument.Parse(b).RootElement.GetProperty("id").GetString();
                }

                _context.SocialPosts.Add(new SocialPost
                {
                    user_id = userId,
                    page_id = pageId,
                    post_id = fbPostId ?? "",
                    message = message,
                    media_url = "",
                    platform = "facebook",
                    created_at = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                return Json(new { success = true, message = "Posted to Facebook!", postId = fbPostId });
            }

            // ─────────────────────────────────────────────────────────────
            // INSTAGRAM
            // ImgBB se public URL → localhost + production dono pe kaam karta hai
            // ─────────────────────────────────────────────────────────────
            if (platform?.ToLower() == "ig" || platform?.ToLower() == "instagram")
            {
                if (images == null || !images.Any())
                    return Json(new { success = false, message = "Instagram requires at least one image." });

                // ImgBB API key check
                var imgbbKey = _config["ImgBB:ApiKey"] ?? "";
                if (string.IsNullOrEmpty(imgbbKey))
                    return Json(new { success = false, message = "ImgBB:ApiKey missing in appsettings.json. Free key: https://api.imgbb.com" });

                // Instagram token — UserTokens table → username=email → instagramtoken
                var igRow = await _context.UserTokens
                    .Where(t => t.username == userEmail
                             && t.instagramtoken != null
                             && t.instagramtoken != "")
                    .OrderByDescending(t => t.CreatedAt)
                    .FirstOrDefaultAsync();

                if (igRow == null)
                    return Json(new { success = false, message = "No Instagram account connected." });

                var igToken = igRow.instagramtoken!;

                // Instagram User ID — /me se fetch karo
                var meR = await http.GetAsync(
                    $"https://graph.instagram.com/me?fields=id,username&access_token={igToken}");
                var meB = await meR.Content.ReadAsStringAsync();
                if (!meR.IsSuccessStatusCode)
                    return Json(new { success = false, message = "IG auth failed: " + meB });

                var igUserId = JsonDocument.Parse(meB).RootElement.GetProperty("id").GetString() ?? "";

                // Images → ImgBB pe upload → public HTTPS URLs
                var publicUrls = new List<string>();
                foreach (var img in images.Take(10))
                {
                    // Image to Base64
                    using var ms = new System.IO.MemoryStream();
                    await img.CopyToAsync(ms);
                    var base64 = Convert.ToBase64String(ms.ToArray());

                    // ImgBB upload
                    var imgbbResp = await http.PostAsync(
                        "https://api.imgbb.com/1/upload",
                        new FormUrlEncodedContent(new[] {
                            new KeyValuePair<string,string>("key",   imgbbKey),
                            new KeyValuePair<string,string>("image", base64)
                        }));

                    var imgbbBody = await imgbbResp.Content.ReadAsStringAsync();
                    if (!imgbbResp.IsSuccessStatusCode)
                        return Json(new { success = false, message = "ImgBB upload failed: " + imgbbBody });

                    var imgUrl = JsonDocument.Parse(imgbbBody)
                        .RootElement.GetProperty("data").GetProperty("url").GetString() ?? "";

                    if (string.IsNullOrEmpty(imgUrl))
                        return Json(new { success = false, message = "ImgBB returned empty URL" });

                    publicUrls.Add(imgUrl);
                }

                string? igPostId = null;

                if (publicUrls.Count == 1)
                {
                    // Single image post
                    // Step 1: Media container
                    var cr = await http.PostAsync(
                        $"https://graph.instagram.com/v19.0/{igUserId}/media"
                        + $"?image_url={Uri.EscapeDataString(publicUrls[0])}"
                        + $"&caption={Uri.EscapeDataString(message ?? "")}"
                        + $"&access_token={igToken}", null);
                    var cb = await cr.Content.ReadAsStringAsync();
                    if (!cr.IsSuccessStatusCode)
                        return Json(new { success = false, message = "IG container failed: " + cb });

                    var cid = JsonDocument.Parse(cb).RootElement.GetProperty("id").GetString() ?? "";

                    // Step 2: Publish
                    var pr = await http.PostAsync(
                        $"https://graph.instagram.com/v19.0/{igUserId}/media_publish"
                        + $"?creation_id={cid}&access_token={igToken}", null);
                    var pb = await pr.Content.ReadAsStringAsync();
                    if (!pr.IsSuccessStatusCode)
                        return Json(new { success = false, message = "IG publish failed: " + pb });

                    igPostId = JsonDocument.Parse(pb).RootElement.GetProperty("id").GetString();
                }
                else
                {
                    // Carousel post (multiple images)
                    // Step 1: Child container for each image
                    var childIds = new List<string>();
                    foreach (var url in publicUrls)
                    {
                        var cr = await http.PostAsync(
                            $"https://graph.instagram.com/v19.0/{igUserId}/media"
                            + $"?image_url={Uri.EscapeDataString(url)}"
                            + $"&is_carousel_item=true"
                            + $"&access_token={igToken}", null);
                        var cb = await cr.Content.ReadAsStringAsync();
                        if (!cr.IsSuccessStatusCode)
                            return Json(new { success = false, message = "IG child container failed: " + cb });

                        childIds.Add(JsonDocument.Parse(cb).RootElement.GetProperty("id").GetString() ?? "");
                    }

                    // Step 2: Carousel container
                    var car = await http.PostAsync(
                        $"https://graph.instagram.com/v19.0/{igUserId}/media"
                        + $"?media_type=CAROUSEL"
                        + $"&children={Uri.EscapeDataString(string.Join(",", childIds))}"
                        + $"&caption={Uri.EscapeDataString(message ?? "")}"
                        + $"&access_token={igToken}", null);
                    var carB = await car.Content.ReadAsStringAsync();
                    if (!car.IsSuccessStatusCode)
                        return Json(new { success = false, message = "IG carousel container failed: " + carB });

                    var carId = JsonDocument.Parse(carB).RootElement.GetProperty("id").GetString() ?? "";

                    // Step 3: Publish carousel
                    var pr = await http.PostAsync(
                        $"https://graph.instagram.com/v19.0/{igUserId}/media_publish"
                        + $"?creation_id={carId}&access_token={igToken}", null);
                    var pb = await pr.Content.ReadAsStringAsync();
                    if (!pr.IsSuccessStatusCode)
                        return Json(new { success = false, message = "IG carousel publish failed: " + pb });

                    igPostId = JsonDocument.Parse(pb).RootElement.GetProperty("id").GetString();
                }

                _context.SocialPosts.Add(new SocialPost
                {
                    user_id = userId,
                    page_id = igUserId,
                    post_id = igPostId ?? "",
                    message = message,
                    media_url = "",
                    platform = "instagram",
                    created_at = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                return Json(new { success = true, message = "Posted to Instagram!", postId = igPostId });
            }

            return Json(new { success = false, message = "Unknown platform: " + platform });
        }
    }

    public class SocialPost
    {
        public int id { get; set; }
        public int? user_id { get; set; }
        public string page_id { get; set; } = "";
        public string post_id { get; set; } = "";
        public string? message { get; set; }
        public string? media_url { get; set; }
        public string platform { get; set; } = "";
        public DateTime created_at { get; set; }
        public string status { get; set; }
    }
}