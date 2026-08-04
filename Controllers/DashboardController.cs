using Azure.Core;
using Google.Apis.Gmail.v1.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using SocialMediaPanel.Services;
using SocialMediaPanel.ViewModels;
using System.Text.Json;

namespace SocialMediaPanel.Controllers
{
    public class DashboardController : Controller
    {
        private readonly AppDbContext _context;
        private readonly PostService _postService;
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ActivePageService _activePages;
        private readonly FacebookService _facebookService;
        private readonly PostPublishingService _publishing;
        private readonly AuditLogService _audit;

        public DashboardController(AppDbContext context, IConfiguration config, IWebHostEnvironment env, PostService postService, ActivePageService activePages, FacebookService facebookService, PostPublishingService publishing, AuditLogService audit)
        {
            _context = context;
            _config = config;
            _postService = postService;
            _activePages = activePages;
            _facebookService = facebookService;
            _publishing = publishing;
            _audit = audit;

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


            var allPosts = await GetAllPosts();

            var posts = allPosts
                .Where(x => x.created_at >= DateTime.Now.AddDays(-7)) // last 7 days
                .OrderByDescending(x => x.created_at)
                .ToList();

            return View(posts);

            //return View();
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



        //public async Task<List<SocialPost>> GetAllPosts()
        //{
        //    List<SocialPost> allPosts = new List<SocialPost>();
        //    var username = HttpContext.Session.GetString("UserEmail");
        //    var fbtoken = await _postService.GetTokenAsync(username);

        //    var httpClient = new HttpClient(); // Best practice: reuse one client
        //    if (!string.IsNullOrEmpty(fbtoken))
        //    {



        //    var page = await _postService.GetFirstPageAsync(fbtoken.ToString());
        //    var (pageId, pageToken) = page.Value;


        //    // 1. ✅ Get Facebook Page Name
        //    string fbPageInfoUrl = $"https://graph.facebook.com/v19.0/{pageId}?fields=name&access_token={pageToken}";
        //    var fbPageResponse = await httpClient.GetStringAsync(fbPageInfoUrl);
        //    dynamic fbPageData = Newtonsoft.Json.JsonConvert.DeserializeObject(fbPageResponse);
        //    string fbPageName = fbPageData.name;

        //    // Fetch Facebook Posts
        //    string fbUrl = $"https://graph.facebook.com/v19.0/{pageId}/posts"
        //                     + $"?fields=message,full_picture,created_time,"
        //                     + $"likes.summary(true),comments.summary(true),shares"
        //                     + $"&access_token={pageToken}";
        //    var fbResponse = await httpClient.GetStringAsync(fbUrl);
        //    dynamic fbData = Newtonsoft.Json.JsonConvert.DeserializeObject(fbResponse);

        //    foreach (var item in fbData.data)
        //    {
        //        allPosts.Add(new SocialPost
        //        {
        //            platform = "facebook",
        //            account_name = fbPageName, // Added this
        //            message = item.message,
        //            media_url = item.full_picture,
        //            created_at = item.created_time,
        //            like_count = item.likes?.summary?.total_count ?? 0,
        //            comment_count = item.comments?.summary?.total_count ?? 0,
        //            share_count = item.shares?.count ?? 0
        //        });
        //    }
        //    }


        //    // 2. ✅ Get Instagram Name
        //    var instatoken = await _postService.GetInstagramTokenAsync(username);


        //    if (!string.IsNullOrEmpty(instatoken))
        //    {

        //        // Note: Use "username" for IG handle or "name" if available in your API scope
        //        string igInfoUrl = $"https://graph.instagram.com/me?fields=username&access_token={instatoken}";
        //        var igInfoResponse = await httpClient.GetStringAsync(igInfoUrl);
        //        dynamic igInfoData = Newtonsoft.Json.JsonConvert.DeserializeObject(igInfoResponse);
        //        string igAccountName = igInfoData.username;

        //        // Fetch Instagram Posts
        //        string igUrl = $"https://graph.instagram.com/me/media?fields=caption,media_url,timestamp,like_count,comments_count&access_token={instatoken}"; var igResponse = await httpClient.GetStringAsync(igUrl);
        //        dynamic igData = Newtonsoft.Json.JsonConvert.DeserializeObject(igResponse);

        //        foreach (var item in igData.data)
        //        {
        //            allPosts.Add(new SocialPost
        //            {
        //                platform = "instagram",
        //                account_name = igAccountName, // Added this
        //                message = item.caption,
        //                media_url = item.media_url,
        //                created_at = item.timestamp,
        //                like_count = item.like_count ?? 0,
        //                comment_count = item.comments_count ?? 0
        //            });
        //        }
        //    }
        //    return allPosts.OrderByDescending(x => x.created_at).ToList();
        //}
        public async Task<List<SocialPost>> GetAllPosts()
        {
            List<SocialPost> allPosts = new List<SocialPost>();
            var userId = HttpContext.Session.GetInt32("UserId");
            var username = HttpContext.Session.GetString("UserEmail") ?? "";
            if (userId == null) return allPosts;

            var httpClient = new HttpClient();

            // 1. Facebook Posts — active page (or first connected page)
            try
            {
                var page = await _activePages.GetActiveFacebookPageAsync(userId.Value);
                if (page != null)
                {
                    string fbUrl = $"https://graph.facebook.com/v19.0/{page.page_id}/posts"
                                 + $"?fields=message,full_picture,created_time,"
                                 + $"likes.summary(true),comments.summary(true),shares"
                                 + $"&access_token={page.page_access_token}";
                    var fbResponse = await httpClient.GetStringAsync(fbUrl);
                    dynamic fbData = Newtonsoft.Json.JsonConvert.DeserializeObject(fbResponse);

                    foreach (var item in fbData.data)
                    {
                        allPosts.Add(new SocialPost
                        {
                            platform = "facebook",
                            account_name = page.page_name,
                            message = item.message,
                            media_url = item.full_picture,
                            created_at = item.created_time,
                            like_count = item.likes?.summary?.total_count ?? 0,
                            comment_count = item.comments?.summary?.total_count ?? 0,
                            share_count = item.shares?.count ?? 0
                        });
                    }
                }
            }
            catch { /* FB fail hone par dashboard break na ho */ }

            // 2. Instagram Posts — active account (or first connected account)
            try
            {
                var igVm = await _postService.GetInstagramPostsAsync(userId.Value, username, null);
                foreach (var row in igVm.Posts)
                {
                    allPosts.Add(new SocialPost
                    {
                        platform = "instagram",
                        account_name = "Instagram",
                        message = row.Message,
                        media_url = row.FullPicture,
                        created_at = row.CreatedTime ?? DateTime.UtcNow,
                        like_count = row.LikesCount,
                        comment_count = row.CommentsCount
                    });
                }
            }
            catch { /* Instagram fail hone par dashboard break na ho */ }

            return allPosts.OrderByDescending(x => x.created_at).ToList();
        }

        [HttpGet]
        public async Task<IActionResult> Leads()
        {

            var userId = HttpContext.Session.GetInt32("UserId");

            var pageIds = await _context.FacebookPages
    .Where(o => o.user_id == userId.ToString())
    .Select(o => o.page_id)
    .Distinct()
    .ToListAsync();


            var leads = await _context.Leads
    .Where(l => pageIds.Contains(l.PageId)) // 🔥 FILTER HERE
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

        // ── CSV EXPORT — Leads (Category A, phase 4) ──────────────────────
        [HttpGet]
        [Route("Leads/Export")]
        public async Task<IActionResult> ExportLeads(string? status)
        {
            var query = _context.Leads.AsQueryable();
            if (!string.IsNullOrEmpty(status) && status != "all")
                query = query.Where(l => l.Status == status);

            var leads = await query.OrderByDescending(l => l.CreatedAt).ToListAsync();

            var csv = CsvExportHelper.BuildCsv(
                new[] { "LeadId", "Name", "Email", "Phone", "Platform", "FormId", "PageId", "Status", "CreatedAt" },
                leads.Select(l => new object?[]
                {
                    l.LeadId, l.FullName, l.Email, l.Phone, l.Platform, l.FormId, l.PageId,
                    l.Status ?? "open", l.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                }));

            return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"leads-{DateTime.UtcNow:yyyyMMdd}.csv");
        }

        [HttpGet]
        [Route("Leads/GetAll")]
        public async Task<IActionResult> GetAll(string? status)
        {
            var query = _context.Leads.AsQueryable();
            if (!string.IsNullOrEmpty(status) && status != "all")
                query = query.Where(l => l.Status == status);

            var leads = await query.OrderByDescending(l => l.CreatedAt).Select(l => new
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
            _audit.Log(HttpContext.Session.GetInt32("UserId"), "lead.status_change", $"leadId={lead.Id} newStatus={request.Status}");
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
            _audit.Log(HttpContext.Session.GetInt32("UserId"), "lead.delete", $"leadId={id} name={lead.FullName} email={lead.Email}");
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
        // SYNC LEADS NOW — manual pull via Graph API (leads_retrieval)
        // Webhook ingestion (WebhookController.FetchAndSaveLead) stays the
        // real-time primary path; this is a demo-friendly backfill button that
        // doesn't depend on live webhook traffic.
        // ════════════════════════════════════════════════════════════════
        [HttpPost]
        [Route("Dashboard/SyncLeadsNow")]
        public async Task<IActionResult> SyncLeadsNow()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null) return Json(new { success = false, message = "Not logged in" });

            var pages = await _activePages.GetAllFacebookPagesAsync(userId.Value);
            if (pages.Count == 0)
                return Json(new { success = false, message = "No Facebook page connected." });

            int added = 0, skipped = 0;

            foreach (var page in pages)
            {
                var forms = await _facebookService.GetLeadFormsAsync(page.page_id, page.page_access_token);

                foreach (var form in forms)
                {
                    var leads = await _facebookService.GetFormLeadsAsync(form.FormId, page.page_access_token);

                    foreach (var lead in leads)
                    {
                        if (await _context.Leads.AnyAsync(l => l.LeadId == lead.LeadId))
                        {
                            skipped++;
                            continue;
                        }

                        string? fullName = null, email = null, phone = null;
                        try
                        {
                            using var doc = JsonDocument.Parse(lead.RawJson);
                            if (doc.RootElement.TryGetProperty("field_data", out var fieldData))
                            {
                                foreach (var field in fieldData.EnumerateArray())
                                {
                                    var name = field.TryGetProperty("name", out var n) ? n.GetString()?.ToLower() : "";
                                    var vals = field.TryGetProperty("values", out var v) ? v : (JsonElement?)null;
                                    var val = vals.HasValue && vals.Value.GetArrayLength() > 0 ? vals.Value[0].GetString() : null;

                                    switch (name)
                                    {
                                        case "full_name":
                                        case "name": fullName = val; break;
                                        case "email":
                                        case "email_address": email = val; break;
                                        case "phone_number":
                                        case "phone": phone = val; break;
                                    }
                                }
                            }
                        }
                        catch { /* keep raw JSON even if field parsing fails */ }

                        _context.Leads.Add(new Lead
                        {
                            LeadId = lead.LeadId,
                            PageId = page.page_id,
                            FormId = form.FormId,
                            FullName = fullName,
                            Email = email,
                            Phone = phone,
                            Platform = "facebook",
                            RawData = lead.RawJson,
                            CreatedAt = DateTime.UtcNow
                        });
                        added++;
                    }
                }
            }

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = added > 0 ? $"Synced {added} new lead(s)." : "No new leads found.",
                added,
                skipped
            });
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



        // ════════════════════════════════════════════════════════════════
        // PUBLISH POST
        // ════════════════════════════════════════════════════════════════
        [HttpPost]
        [RequestSizeLimit(104857600)]
        [RequestFormLimits(MultipartBodyLengthLimit = 104857600)]
        public async Task<IActionResult> PublishPost(
            List<IFormFile> images, string message, string platform, string campaign, bool isDraft = false, string? pageId = null, DateTime? scheduledAt = null)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return Json(new { success = false, message = "User not logged in" });

            // ── STEP 1: Images → bytes mein load ─────────────────────
            var imageDataList = new List<(byte[] bytes, string contentType, string fileName)>();
            if (images != null && images.Count > 0)
            {
                foreach (var img in images)
                {
                    if (img.Length > 0)
                    {
                        using var ms = new System.IO.MemoryStream();
                        await img.CopyToAsync(ms);
                        imageDataList.Add((ms.ToArray(), img.ContentType, img.FileName));
                    }
                }
            }

            // ── STEP 2: wwwroot/uploads mein save ────────────────────
            var uploadsDir = System.IO.Path.Combine(_env.WebRootPath, "uploads");
            System.IO.Directory.CreateDirectory(uploadsDir);

            var savedPaths = new List<string>();
            var savedFullUrls = new List<string>();

            var baseUrl = _config["AppBaseUrl"]?.TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl))
                baseUrl = $"{Request.Scheme}://{Request.Host}";

            foreach (var (bytes, contentType, fileName) in imageDataList)
            {
                var ext = contentType.ToLower() switch
                {
                    "image/jpeg" => ".jpg",
                    "image/jpg" => ".jpg",
                    "image/png" => ".png",
                    "image/gif" => ".gif",
                    "image/webp" => ".webp",
                    "video/mp4" => ".mp4",
                    "video/mov" => ".mov",
                    _ => System.IO.Path.GetExtension(fileName).ToLower()
                };
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";

                var newName = Guid.NewGuid().ToString("N") + ext;
                var savePath = System.IO.Path.Combine(uploadsDir, newName);
                await System.IO.File.WriteAllBytesAsync(savePath, bytes);
                savedPaths.Add("/uploads/" + newName);
                savedFullUrls.Add($"{baseUrl}/uploads/{newName}");
            }

            // ── STEP 3: DRAFT ─────────────────────────────────────────
            if (isDraft)
            {
                _context.SocialPosts.Add(new SocialPost
                {
                    user_id = (int)userId,
                    page_id = pageId ?? "",
                    post_id = "",
                    message = message,
                    media_url = string.Join(",", savedPaths),
                    platform = platform,
                    status = "draft",
                    created_at = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                return Json(new { success = true, message = "Draft saved!" });
            }

            // ── STEP 3b: SCHEDULED — save for ScheduledPostPublisher to pick up ──
            if (scheduledAt.HasValue && scheduledAt.Value > DateTime.UtcNow)
            {
                _context.SocialPosts.Add(new SocialPost
                {
                    user_id = (int)userId,
                    page_id = pageId ?? "",
                    post_id = "",
                    message = message,
                    media_url = string.Join(",", savedPaths),
                    platform = platform,
                    status = "scheduled",
                    scheduled_at = scheduledAt.Value,
                    created_at = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                _audit.Log((int)userId, "post.schedule", $"platform={platform} pageId={pageId} scheduledAt={scheduledAt.Value:o}");
                return Json(new { success = true, message = $"Post scheduled for {scheduledAt.Value:dd MMM yyyy, hh:mm tt} UTC." });
            }

            // ── STEP 4: PUBLISH NOW — via the shared PostPublishingService ──
            var isFacebook = platform?.ToLower() == "fb" || platform?.ToLower() == "facebook";
            var isInstagram = platform?.ToLower() == "ig" || platform?.ToLower() == "instagram";

            if (!isFacebook && !isInstagram)
                return Json(new { success = false, message = "Unknown platform: " + platform });

            var result = isFacebook
                ? await _publishing.PublishToFacebookAsync(userId.Value, pageId, message, imageDataList)
                : await _publishing.PublishToInstagramAsync(userId.Value, pageId, message, baseUrl, imageDataList, savedFullUrls);

            if (!result.Success)
                return Json(new { success = false, message = result.Message });

            _context.SocialPosts.Add(new SocialPost
            {
                user_id = (int)userId,
                page_id = result.ResolvedPageId ?? pageId ?? "",
                post_id = result.PostId ?? "",
                message = message,
                media_url = string.Join(",", savedPaths),
                platform = isFacebook ? "facebook" : "instagram",
                status = "published",
                created_at = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
            _audit.Log((int)userId, "post.publish", $"platform={(isFacebook ? "facebook" : "instagram")} pageId={result.ResolvedPageId ?? pageId} postId={result.PostId}");

            return Json(new { success = true, message = result.Message, postId = result.PostId });
        }


    }



    public class SocialPost
    {
        public int id { get; set; }
        public int user_id { get; set; }
        public string? page_id { get; set; }
        public string? account_name { get; set; }
        public string? post_id { get; set; }
        public string? message { get; set; }


        public string? media_url { get; set; }
        public string platform { get; set; } = "";
        public DateTime created_at { get; set; }

        public int? like_count { get; set; }
        public int? comment_count { get; set; }
        public string? status { get; set; }
        public int? share_count { get; set; }

        // Post Scheduling (Category A, phase 1) — maps to the scheduled_at
        // column added at startup in Program.cs (no EF migrations in this
        // repo — see project notes on why that's a raw idempotent ALTER
        // instead of a real migration).
        public DateTime? scheduled_at { get; set; }
    }

}