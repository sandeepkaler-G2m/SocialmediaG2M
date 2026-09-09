using Microsoft.AspNetCore.Mvc;
using SocialMediaPanel.Services;

namespace SocialMediaPanel.Controllers
{
    /// <summary>
    /// Standalone WhatsApp-PDF relay feature. Kept fully separate from the rest of the
    /// panel: own controller, own services (GoogleDrivePdfService, WhatsAppSendService),
    /// own storage folder (wwwroot/pdf-relay). One API: POST /api/pdf-relay/send-whatsapp.
    ///
    /// Flow (single call): caller POSTs a Google Drive share link + WhatsApp send
    /// details -> we download the PDF and re-host it under wwwroot -> the resulting
    /// public URL is fed into the WhatsApp send-message API as the outgoing document's
    /// "link".
    ///
    /// Intentionally has no login/session check: this needs to be callable server-to-
    /// server, and the file it hands back must be fetchable by WhatsApp's own servers
    /// (no cookie/session they could present). Treat the route itself as the only
    /// protection for now — revisit if this needs to be locked down further later.
    /// </summary>
    [ApiController]
    [Route("api/pdf-relay")]
    public class WhatsAppPdfController : ControllerBase
    {
        private const string StorageFolder = "pdf-relay";

        private readonly GoogleDrivePdfService _drive;
        private readonly WhatsAppSendService _whatsapp;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly ILogger<WhatsAppPdfController> _logger;

        public WhatsAppPdfController(
            GoogleDrivePdfService drive,
            WhatsAppSendService whatsapp,
            IWebHostEnvironment env,
            IConfiguration config,
            ILogger<WhatsAppPdfController> logger)
        {
            _drive = drive;
            _whatsapp = whatsapp;
            _env = env;
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Full payload for POST /api/pdf-relay/send-whatsapp — every value here is
        /// caller-supplied per request; nothing is a fixed curl. driveLink is a request
        /// field too (a new/different Drive link per call is expected) — only apiKey is
        /// optional (falls back to WhatsAppRelay:DefaultApiKey in config).
        /// </summary>
        public class SendWhatsAppPdfRequest
        {
            public string DriveLink { get; set; } = "";
            public string Destination { get; set; } = "";
            public string CampaignName { get; set; } = "";
            public List<string> TemplateParams { get; set; } = new();
            public string? ApiKey { get; set; }
            public string Type { get; set; } = "template";
            public string MediaType { get; set; } = "DOCUMENT";
        }

        /// <summary>
        /// POST /api/pdf-relay/send-whatsapp — the full pipeline in one call:
        /// Drive link -> download -> public URL (with domain) -> WhatsApp send-message
        /// API, using a payload built from the request body (nothing hardcoded here;
        /// only the send URL + a fallback apiKey come from config).
        /// </summary>
        [HttpPost("send-whatsapp")]
        public async Task<IActionResult> SendWhatsAppPdf([FromBody] SendWhatsAppPdfRequest body)
        {
            if (body == null)
                return BadRequest(new { success = false, message = "body required" });
            if (string.IsNullOrWhiteSpace(body.Destination))
                return BadRequest(new { success = false, message = "destination required" });

            var (ok, error, status, hosted) = await FetchAndHost(body.DriveLink);
            if (!ok) return StatusCode(status, error);

            var payload = new WhatsAppSendService.WhatsAppSendPayload
            {
                ApiKey = body.ApiKey ?? "",
                CampaignName = body.CampaignName,
                Destination = body.Destination,
                Type = string.IsNullOrWhiteSpace(body.Type) ? "template" : body.Type,
                MediaType = string.IsNullOrWhiteSpace(body.MediaType) ? "DOCUMENT" : body.MediaType,
                TemplateParams = body.TemplateParams ?? new List<string>(),
                Media = new WhatsAppSendService.WhatsAppMedia { Url = hosted!.PublicUrl }
            };

            try
            {
                var (sendOk, sendStatus, raw) = await _whatsapp.SendAsync(payload, HttpContext.RequestAborted);
                return StatusCode(sendOk ? 200 : 502, new
                {
                    success = sendOk,
                    publicUrl = hosted.PublicUrl,
                    fileName = hosted.FileName,
                    sizeBytes = hosted.SizeBytes,
                    whatsappStatus = sendStatus,
                    whatsappResponse = TryParseJson(raw)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PDF relay: WhatsApp send failed for destination {Destination}", body.Destination);
                return StatusCode(502, new { success = false, message = "WhatsApp send fail: " + ex.Message, publicUrl = hosted.PublicUrl });
            }
        }

        private static object TryParseJson(string raw)
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<object>(raw)!; }
            catch { return raw; }
        }

        private record HostedFile(string PublicUrl, string FileName, long SizeBytes);

        /// <summary>
        /// Downloads the Drive PDF, saves it under wwwroot/pdf-relay, and builds the
        /// public URL for it.
        /// </summary>
        private async Task<(bool Ok, object? Error, int Status, HostedFile? Result)> FetchAndHost(string driveLink)
        {
            if (string.IsNullOrWhiteSpace(driveLink))
                return (false, new { success = false, message = "driveLink required" }, 400, null);

            var fileId = GoogleDrivePdfService.ExtractFileId(driveLink);
            if (fileId == null)
                return (false, new { success = false, message = "Drive link se file id nahi mil paayi" }, 400, null);

            byte[] bytes;
            string? driveFileName;
            try
            {
                (bytes, _, driveFileName) = await _drive.DownloadAsync(fileId, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PDF relay: Drive download failed for id {FileId}", fileId);
                return (false, new { success = false, message = "Drive se download fail: " + ex.Message }, 502, null);
            }

            var dir = Path.Combine(_env.WebRootPath, StorageFolder);
            Directory.CreateDirectory(dir);

            // Use the file's actual Drive name where possible — only fall back to a
            // generated name if Drive didn't send one, or to avoid clobbering an
            // existing file that happens to share the same name.
            var fileName = GoogleDrivePdfService.SanitizeFileName(driveFileName);
            var savePath = Path.Combine(dir, fileName);
            if (System.IO.File.Exists(savePath))
            {
                var stem = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                fileName = $"{stem}_{Guid.NewGuid().ToString("N")[..6]}{ext}";
                savePath = Path.Combine(dir, fileName);
            }
            await System.IO.File.WriteAllBytesAsync(savePath, bytes);

            var baseUrl = _config["AppBaseUrl"]?.TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl))
                baseUrl = $"{Request.Scheme}://{Request.Host}";

            var publicUrl = $"{baseUrl}/{StorageFolder}/{fileName}";

            _logger.LogInformation("PDF relay: fetched {FileId} -> {PublicUrl} ({Size} bytes)", fileId, publicUrl, bytes.Length);

            return (true, null, 200, new HostedFile(publicUrl, fileName, bytes.Length));
        }
    }
}
