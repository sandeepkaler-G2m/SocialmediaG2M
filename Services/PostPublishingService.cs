using System.Text.Json;

namespace SocialMediaPanel.Services
{
    public class PublishResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string? PostId { get; set; }
        public string? ResolvedPageId { get; set; }
    }

    /// <summary>
    /// Facebook/Instagram post publishing, extracted out of
    /// DashboardController.PublishPost so both the controller (immediate
    /// publish) and ScheduledPostPublisher (background, delayed publish) call
    /// the exact same tested code path instead of two copies drifting apart.
    /// </summary>
    public class PostPublishingService
    {
        private readonly ActivePageService _activePages;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly NativeInstagramService _nativeInstagram;

        public PostPublishingService(ActivePageService activePages, IConfiguration config, IHttpClientFactory httpClientFactory, NativeInstagramService nativeInstagram)
        {
            _activePages = activePages;
            _config = config;
            _httpClientFactory = httpClientFactory;
            _nativeInstagram = nativeInstagram;
        }

        // ─────────────────────────────────────────────────────────────────
        // FACEBOOK
        // ─────────────────────────────────────────────────────────────────
        public async Task<PublishResult> PublishToFacebookAsync(
            int userId, string? pageId, string? message,
            List<(byte[] bytes, string contentType, string fileName)> imageDataList)
        {
            var page = await _activePages.GetActiveFacebookPageAsync(userId, pageId);
            if (page == null)
                return new PublishResult { Success = false, Message = "No Facebook page connected." };

            string fbPageId = page.page_id;
            string pageToken = page.page_access_token;
            string? fbPostId = null;

            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(120);

            try
            {
                if (imageDataList.Count == 1)
                {
                    var (bytes, contentType, fileName) = imageDataList[0];

                    var imageContent = new ByteArrayContent(bytes);
                    imageContent.Headers.ContentType =
                        System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

                    var mc = new MultipartFormDataContent();
                    mc.Add(imageContent, "source", fileName);
                    mc.Add(new StringContent(message ?? ""), "caption");

                    var r = await http.PostAsync(
                        $"https://graph.facebook.com/v19.0/{fbPageId}/photos?access_token={pageToken}", mc);
                    var b = await r.Content.ReadAsStringAsync();
                    if (!r.IsSuccessStatusCode)
                        return new PublishResult { Success = false, Message = "FB upload failed: " + b, ResolvedPageId = fbPageId };

                    fbPostId = JsonDocument.Parse(b).RootElement.GetProperty("id").GetString();
                }
                else if (imageDataList.Count > 1)
                {
                    var photoIds = new List<string>();
                    foreach (var (bytes, contentType, fileName) in imageDataList.Take(10))
                    {
                        var imageContent = new ByteArrayContent(bytes);
                        imageContent.Headers.ContentType =
                            System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

                        var mc = new MultipartFormDataContent();
                        mc.Add(imageContent, "source", fileName);
                        mc.Add(new StringContent("false"), "published");

                        var r = await http.PostAsync(
                            $"https://graph.facebook.com/v19.0/{fbPageId}/photos?access_token={pageToken}", mc);
                        var b = await r.Content.ReadAsStringAsync();
                        if (!r.IsSuccessStatusCode)
                            return new PublishResult { Success = false, Message = "FB image upload failed: " + b, ResolvedPageId = fbPageId };

                        photoIds.Add(JsonDocument.Parse(b).RootElement.GetProperty("id").GetString() ?? "");
                    }

                    var feed = new MultipartFormDataContent();
                    feed.Add(new StringContent(message ?? ""), "message");
                    feed.Add(new StringContent(pageToken), "access_token");
                    for (int i = 0; i < photoIds.Count; i++)
                        feed.Add(new StringContent($"{{\"media_fbid\":\"{photoIds[i]}\"}}"), $"attached_media[{i}]");

                    var fr = await http.PostAsync($"https://graph.facebook.com/v19.0/{fbPageId}/feed", feed);
                    var fb2 = await fr.Content.ReadAsStringAsync();
                    if (!fr.IsSuccessStatusCode)
                        return new PublishResult { Success = false, Message = "FB multi-image post failed: " + fb2, ResolvedPageId = fbPageId };

                    fbPostId = JsonDocument.Parse(fb2).RootElement.GetProperty("id").GetString();
                }
                else
                {
                    var fd = new FormUrlEncodedContent(new[] {
                        new KeyValuePair<string,string>("message",      message ?? ""),
                        new KeyValuePair<string,string>("access_token", pageToken)
                    });
                    var r = await http.PostAsync($"https://graph.facebook.com/v19.0/{fbPageId}/feed", fd);
                    var b = await r.Content.ReadAsStringAsync();
                    if (!r.IsSuccessStatusCode)
                        return new PublishResult { Success = false, Message = "FB post failed: " + b, ResolvedPageId = fbPageId };

                    fbPostId = JsonDocument.Parse(b).RootElement.GetProperty("id").GetString();
                }
            }
            catch (Exception ex)
            {
                return new PublishResult { Success = false, Message = "FB post failed: " + ex.Message, ResolvedPageId = fbPageId };
            }

            return new PublishResult { Success = true, Message = "Posted to Facebook!", PostId = fbPostId, ResolvedPageId = fbPageId };
        }

        // ─────────────────────────────────────────────────────────────────
        // INSTAGRAM
        // ─────────────────────────────────────────────────────────────────
        public async Task<PublishResult> PublishToInstagramAsync(
            int userId, string? pageId, string? message, string baseUrl,
            List<(byte[] bytes, string contentType, string fileName)> imageDataList,
            List<string> savedFullUrlsIn)
        {
            if (imageDataList.Count == 0)
                return new PublishResult { Success = false, Message = "Instagram requires at least one image." };

            var igAccount = await _activePages.GetActiveInstagramAccountAsync(userId, pageId);
            if (igAccount == null)
                return new PublishResult { Success = false, Message = "No Instagram account connected." };

            // Native-login accounts (direct "Instagram API with Instagram Login")
            // carry their own token and use graph.instagram.com — no Facebook
            // Page to resolve a token from at all.
            bool isNative = !string.IsNullOrEmpty(igAccount.NativeAccessToken);
            string igToken;
            string apiBase;

            if (isNative)
            {
                igToken = igAccount.NativeAccessToken!;
                apiBase = "https://graph.instagram.com/v21.0";
            }
            else
            {
                var fbPage = await _activePages.GetLinkedPageForInstagramAsync(userId, igAccount.InstagramUserId);
                if (fbPage == null)
                    return new PublishResult { Success = false, Message = "Instagram not linked with a Facebook page." };

                igToken = fbPage.page_access_token ?? "";
                apiBase = "https://graph.facebook.com/v19.0";
            }

            string igUserId = igAccount.InstagramUserId;

            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(120);

            var savedFullUrls = new List<string>(savedFullUrlsIn);

            try
            {
                if (baseUrl.Contains("localhost") || baseUrl.Contains("127.0.0.1"))
                {
                    var imgbbKey = _config["ImgBB:ApiKey"] ?? "";
                    if (string.IsNullOrEmpty(imgbbKey))
                        return new PublishResult { Success = false, Message = "ImgBB key missing in appsettings.json", ResolvedPageId = igUserId };

                    savedFullUrls.Clear();

                    foreach (var (bytes, _, _) in imageDataList)
                    {
                        var base64 = Convert.ToBase64String(bytes);

                        var imgbbResp = await http.PostAsync(
                            "https://api.imgbb.com/1/upload",
                            new FormUrlEncodedContent(new[] {
                                new KeyValuePair<string,string>("key", imgbbKey),
                                new KeyValuePair<string,string>("image", base64)
                            }));

                        var imgbbBody = await imgbbResp.Content.ReadAsStringAsync();
                        if (!imgbbResp.IsSuccessStatusCode)
                            return new PublishResult { Success = false, Message = "ImgBB upload failed: " + imgbbBody, ResolvedPageId = igUserId };

                        var imgUrl = JsonDocument.Parse(imgbbBody)
                            .RootElement.GetProperty("data").GetProperty("url").GetString() ?? "";
                        savedFullUrls.Add(imgUrl);
                    }
                }

                string igPostId = "";

                if (savedFullUrls.Count == 1)
                {
                    var createRes = await http.PostAsync(
                        $"{apiBase}/{igUserId}/media" +
                        $"?image_url={Uri.EscapeDataString(savedFullUrls[0])}" +
                        $"&caption={Uri.EscapeDataString(message ?? "")}" +
                        $"&access_token={igToken}", null);

                    var createBody = await createRes.Content.ReadAsStringAsync();
                    if (!createRes.IsSuccessStatusCode)
                        return new PublishResult { Success = false, Message = "IG container failed: " + createBody, ResolvedPageId = igUserId };

                    var creationId = JsonDocument.Parse(createBody).RootElement.GetProperty("id").GetString() ?? "";

                    var publishRes = await http.PostAsync(
                        $"{apiBase}/{igUserId}/media_publish" +
                        $"?creation_id={creationId}&access_token={igToken}", null);

                    var publishBody = await publishRes.Content.ReadAsStringAsync();
                    if (!publishRes.IsSuccessStatusCode)
                        return new PublishResult { Success = false, Message = "IG publish failed: " + publishBody, ResolvedPageId = igUserId };

                    igPostId = JsonDocument.Parse(publishBody).RootElement.GetProperty("id").GetString() ?? "";
                }
                else
                {
                    var childIds = new List<string>();
                    foreach (var url in savedFullUrls)
                    {
                        var childRes = await http.PostAsync(
                            $"{apiBase}/{igUserId}/media" +
                            $"?image_url={Uri.EscapeDataString(url)}" +
                            $"&is_carousel_item=true" +
                            $"&access_token={igToken}", null);

                        var childBody = await childRes.Content.ReadAsStringAsync();
                        if (!childRes.IsSuccessStatusCode)
                            return new PublishResult { Success = false, Message = "IG child failed: " + childBody, ResolvedPageId = igUserId };

                        var childId = JsonDocument.Parse(childBody).RootElement.GetProperty("id").GetString() ?? "";
                        childIds.Add(childId);
                    }

                    var carouselRes = await http.PostAsync(
                        $"{apiBase}/{igUserId}/media" +
                        $"?media_type=CAROUSEL" +
                        $"&children={Uri.EscapeDataString(string.Join(",", childIds))}" +
                        $"&caption={Uri.EscapeDataString(message ?? "")}" +
                        $"&access_token={igToken}", null);

                    var carouselBody = await carouselRes.Content.ReadAsStringAsync();
                    if (!carouselRes.IsSuccessStatusCode)
                        return new PublishResult { Success = false, Message = "IG carousel failed: " + carouselBody, ResolvedPageId = igUserId };

                    var carouselId = JsonDocument.Parse(carouselBody).RootElement.GetProperty("id").GetString() ?? "";

                    var publishRes = await http.PostAsync(
                        $"{apiBase}/{igUserId}/media_publish" +
                        $"?creation_id={carouselId}&access_token={igToken}", null);

                    var publishBody = await publishRes.Content.ReadAsStringAsync();
                    if (!publishRes.IsSuccessStatusCode)
                        return new PublishResult { Success = false, Message = "IG publish failed: " + publishBody, ResolvedPageId = igUserId };

                    igPostId = JsonDocument.Parse(publishBody).RootElement.GetProperty("id").GetString() ?? "";
                }

                return new PublishResult { Success = true, Message = "Posted to Instagram!", PostId = igPostId, ResolvedPageId = igUserId };
            }
            catch (Exception ex)
            {
                return new PublishResult { Success = false, Message = "IG post failed: " + ex.Message, ResolvedPageId = igUserId };
            }
        }
    }
}
