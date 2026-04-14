
﻿using Azure.Core;
﻿using Google.Apis.Gmail.v1.Data;
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
        private readonly LinkedInService _linkedin;


        public DashboardController(AppDbContext context, IConfiguration config, IWebHostEnvironment env, LinkedInService linkedInService, PostService postService)
        {
            _context = context;
            _config = config;
            _postService = postService;
            _linkedin = linkedInService;
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



        public async Task<List<SocialPost>> GetAllPosts()
        {
            List<SocialPost> allPosts = new List<SocialPost>();
            var username = HttpContext.Session.GetString("UserEmail");
            var fbtoken = await _postService.GetTokenAsync(username);

            var httpClient = new HttpClient(); // Best practice: reuse one client
            if (!string.IsNullOrEmpty(fbtoken))
            {



                var page = await _postService.GetFirstPageAsync(fbtoken.ToString());
                var (pageId, pageToken) = page.Value;


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
            }


            // 2. ✅ Get Instagram Name
            var instatoken = await _postService.GetInstagramTokenAsync(username);


            if (!string.IsNullOrEmpty(instatoken))
            {

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
            }
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



        // ════════════════════════════════════════════════════════════════
        // PUBLISH POST
        // ════════════════════════════════════════════════════════════════
        [HttpPost]
        [RequestSizeLimit(104857600)]
        [RequestFormLimits(MultipartBodyLengthLimit = 104857600)]
        public async Task<IActionResult> PublishPost(
            List<IFormFile> images, string message, string platform, string campaign, bool isDraft = false)
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return Json(new { success = false, message = "User not logged in" });

            var userEmail = HttpContext.Session.GetString("UserEmail") ?? "";

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
                // fileName blob ho sakta hai — contentType se extension lo
                var ext = contentType.ToLower() switch
                {
                    "image/jpeg" => ".jpg",
                    "image/jpg" => ".jpg",
                    "image/png" => ".png",
                    "image/gif" => ".gif",
                    "image/webp" => ".webp",
                    "video/mp4" => ".mp4",
                    "video/mov" => ".mov",
                    _ => System.IO.Path.GetExtension(fileName).ToLower()  // fallback
                };

                // Agar ext empty hai toh .jpg default
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
                    user_id = userId,
                    page_id = "",
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

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(120); // timeout badhao

            // ─────────────────────────────────────────────────────────
            // FACEBOOK
            // FIX: ByteArrayContent sahi tarike se banao
            // ─────────────────────────────────────────────────────────
            if (platform?.ToLower() == "fb" || platform?.ToLower() == "facebook")
            {
                var page = await _context.FacebookPages
                    .Where(x => x.user_id == userId.ToString())
                    .OrderByDescending(x => x.id)
                    .FirstOrDefaultAsync();

                if (page == null)
                    return Json(new { success = false, message = "No Facebook page connected." });

                string pageId = page.page_id;
                string pageToken = page.page_access_token;
                string? fbPostId = null;

                if (imageDataList.Count == 1)
                {
                    // Single image
                    var (bytes, contentType, fileName) = imageDataList[0];

                    // FIX: ByteArrayContent alag banao, phir header set karo
                    var imageContent = new ByteArrayContent(bytes);
                    imageContent.Headers.ContentType =
                        System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

                    var mc = new MultipartFormDataContent();
                    mc.Add(imageContent, "source", fileName);
                    mc.Add(new StringContent(message ?? ""), "caption");

                    var r = await http.PostAsync(
                        $"https://graph.facebook.com/v19.0/{pageId}/photos?access_token={pageToken}", mc);
                    var b = await r.Content.ReadAsStringAsync();
                    if (!r.IsSuccessStatusCode)
                        return Json(new { success = false, message = "FB upload failed: " + b });

                    fbPostId = JsonDocument.Parse(b).RootElement.GetProperty("id").GetString();
                }
                else if (imageDataList.Count > 1)
                {
                    // Multiple images → unpublished → one feed post
                    var photoIds = new List<string>();
                    foreach (var (bytes, contentType, fileName) in imageDataList.Take(10))
                    {
                        // FIX: ByteArrayContent alag banao
                        var imageContent = new ByteArrayContent(bytes);
                        imageContent.Headers.ContentType =
                            System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

                        var mc = new MultipartFormDataContent();
                        mc.Add(imageContent, "source", fileName);
                        mc.Add(new StringContent("false"), "published");

                        var r = await http.PostAsync(
                            $"https://graph.facebook.com/v19.0/{pageId}/photos?access_token={pageToken}", mc);
                        var b = await r.Content.ReadAsStringAsync();
                        if (!r.IsSuccessStatusCode)
                            return Json(new { success = false, message = "FB image upload failed: " + b });

                        photoIds.Add(JsonDocument.Parse(b).RootElement.GetProperty("id").GetString() ?? "");
                    }

                    // One post with all photos
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
                    media_url = string.Join(",", savedPaths),
                    platform = "facebook",
                    status = "published",
                    created_at = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                return Json(new { success = true, message = "Posted to Facebook!", postId = fbPostId });
            }

            // ─────────────────────────────────────────────────────────
            // INSTAGRAM
            // FIX: imageDataList.Count check — savedFullUrls pe depend karo
            // ─────────────────────────────────────────────────────────
            if (platform?.ToLower() == "ig" || platform?.ToLower() == "instagram")
            {
                // FIX: imageDataList check karo — savedFullUrls nahi
                if (imageDataList.Count == 0)
                    return Json(new { success = false, message = "Instagram requires at least one image." });

                // Instagram token
                var igRow = await _context.UserTokens
                    .Where(t => t.username == userEmail
                             && t.instagramtoken != null
                             && t.instagramtoken != "")
                    .OrderByDescending(t => t.CreatedAt)
                    .FirstOrDefaultAsync();

                if (igRow == null)
                    return Json(new { success = false, message = "No Instagram account connected." });

                var igToken = igRow.instagramtoken!;

                // Instagram User ID
                var meR = await http.GetAsync(
                    $"https://graph.instagram.com/me?fields=id,username&access_token={igToken}");
                var meB = await meR.Content.ReadAsStringAsync();
                if (!meR.IsSuccessStatusCode)
                    return Json(new { success = false, message = "IG auth failed: " + meB });

                var igUserId = JsonDocument.Parse(meB).RootElement.GetProperty("id").GetString() ?? "";

                // FIX: Localhost pe savedFullUrls localhost URL hoga — override with ImgBB
                if (baseUrl.Contains("localhost") || baseUrl.Contains("127.0.0.1"))
                {
                    var imgbbKey = _config["ImgBB:ApiKey"] ?? "";
                    if (string.IsNullOrEmpty(imgbbKey))
                        return Json(new { success = false, message = "Localhost pe Instagram test ke liye ImgBB:ApiKey set karo appsettings.json mein." });

                    savedFullUrls.Clear();
                    foreach (var (bytes, _, _) in imageDataList)
                    {
                        var base64 = Convert.ToBase64String(bytes);
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
                        savedFullUrls.Add(imgUrl);
                    }
                }

                string? igPostId = null;

                if (savedFullUrls.Count == 1)
                {
                    // Single image
                    var cr = await http.PostAsync(
                        $"https://graph.instagram.com/v19.0/{igUserId}/media"
                        + $"?image_url={Uri.EscapeDataString(savedFullUrls[0])}"
                        + $"&caption={Uri.EscapeDataString(message ?? "")}"
                        + $"&access_token={igToken}", null);
                    var cb = await cr.Content.ReadAsStringAsync();
                    if (!cr.IsSuccessStatusCode)
                        return Json(new { success = false, message = "IG container failed: " + cb });

                    var cid = JsonDocument.Parse(cb).RootElement.GetProperty("id").GetString() ?? "";

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
                    // Carousel
                    var childIds = new List<string>();
                    foreach (var url in savedFullUrls)
                    {
                        var cr = await http.PostAsync(
                            $"https://graph.instagram.com/v19.0/{igUserId}/media"
                            + $"?image_url={Uri.EscapeDataString(url)}"
                            + $"&is_carousel_item=true"
                            + $"&access_token={igToken}", null);
                        var cb = await cr.Content.ReadAsStringAsync();
                        if (!cr.IsSuccessStatusCode)
                            return Json(new { success = false, message = "IG child failed: " + cb });
                        childIds.Add(JsonDocument.Parse(cb).RootElement.GetProperty("id").GetString() ?? "");
                    }

                    var car = await http.PostAsync(
                        $"https://graph.instagram.com/v19.0/{igUserId}/media"
                        + $"?media_type=CAROUSEL"
                        + $"&children={Uri.EscapeDataString(string.Join(",", childIds))}"
                        + $"&caption={Uri.EscapeDataString(message ?? "")}"
                        + $"&access_token={igToken}", null);
                    var carB = await car.Content.ReadAsStringAsync();
                    if (!car.IsSuccessStatusCode)
                        return Json(new { success = false, message = "IG carousel failed: " + carB });

                    var carId = JsonDocument.Parse(carB).RootElement.GetProperty("id").GetString() ?? "";

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
                    media_url = string.Join(",", savedPaths),
                    platform = "instagram",
                    status = "published",
                    created_at = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                return Json(new { success = true, message = "Posted to Instagram!", postId = igPostId });
            }


         

                if (platform?.ToLower() == "li" || platform?.ToLower() == "linkedin")
                {
                    if (string.IsNullOrWhiteSpace(message))
                        return Json(new { success = false, message = "Post text is required." });

                    if (message.Length > 3000)
                        return Json(new { success = false, message = "Post exceeds 3000 character limit." });

                    var integration = await _context.LinkedInIntegrations
                        .FirstOrDefaultAsync(l => l.UserId == userId.ToString() && l.IsActive);

                    if (integration == null)
                        return Json(new { success = false, message = "LinkedIn not connected." });

                    // Refresh token
                    var token = await _EnsureFreshTokenAsync(integration);
                    if (string.IsNullOrEmpty(token))
                        return Json(new { success = false, message = "LinkedIn token expired — reconnect required." });

                    // Image handling (take first image only — LinkedIn supports single image in simple API)
                    string? imageBase64 = null;
                    string? imageMime = null;

                    if (imageDataList.Count > 0)
                    {
                        var (bytes, contentType, _) = imageDataList[0];
                        imageBase64 = Convert.ToBase64String(bytes);
                        imageMime = contentType;
                    }

                    var record = new LinkedinPosts
                    {
                        UserId = userId.ToString(),
                        PostText = message,
                        ArticleUrl = null,
                        CreatedAt = DateTime.UtcNow
                    };

                    try
                    {
                        var postId = await _linkedin.PostAsync(
                            token,
                            integration.LinkedInUserId,
                            message,
                            null,
                            imageBase64,
                            imageMime
                        );

                        record.PostId = postId;
                        record.Status = "posted";
                        record.PostedAt = DateTime.UtcNow;

                        _context.LinkedInPosts.Add(record);

                        // ALSO SAVE IN SocialPosts (same as FB & IG)
                        _context.SocialPosts.Add(new SocialPost
                        {
                            user_id = userId,
                            page_id = integration.LinkedInUserId,
                            post_id = postId,
                            message = message,
                            media_url = string.Join(",", savedPaths),
                            platform = "linkedin",
                            status = "published",
                            created_at = DateTime.UtcNow
                        });

                        await _context.SaveChangesAsync();
                        await _context.SaveChangesAsync();

                        return Json(new
                        {
                            success = true,
                            message = "Posted to LinkedIn!",
                            postId = postId
                        });
                    }
                    catch (Exception ex)
                    {
                        record.Status = "failed";
                        record.ErrorMessage = ex.Message;

                        _context.LinkedInPosts.Add(record);
                        await _context.SaveChangesAsync();

                        return Json(new
                        {
                            success = false,
                            message = "LinkedIn post failed: " + ex.Message
                        });
                    }
                }
            

            return Json(new { success = false, message = "Unknown platform: " + platform });
        }

        private async Task<string?> _EnsureFreshTokenAsync(LinkedInIntegration integration)
        {
            if (integration.TokenExpiresAt.HasValue &&
                integration.TokenExpiresAt.Value > DateTime.UtcNow.AddMinutes(5))
                return integration.AccessToken;

            if (string.IsNullOrEmpty(integration.RefreshToken))
                return null;

            try
            {
                var (newToken, expiresIn) = await _linkedin.RefreshTokenAsync(integration.RefreshToken);
                integration.AccessToken = newToken;
                integration.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
                await _context.SaveChangesAsync();
                return newToken;
            }
            catch { return null; }
        }

    }
    public class SocialPost
    {
        public int id { get; set; }
        public int? user_id { get; set; }
        public string page_id { get; set; }
        public string? account_name { get; set; }
        public string post_id { get; set; }
        public string message { get; set; }


        public string? media_url { get; set; }
        public string platform { get; set; } = "";
        public DateTime created_at { get; set; }

        public int? like_count { get; set; }
        public int? comment_count { get; set; }
        public string? status { get; set; }
        public int? share_count { get; set; }
    }

}

