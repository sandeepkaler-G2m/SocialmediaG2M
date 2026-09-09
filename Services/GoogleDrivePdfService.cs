using System.Text.RegularExpressions;

namespace SocialMediaPanel.Services
{
    /// <summary>
    /// Standalone helper for the WhatsApp-PDF-relay feature (see WhatsAppPdfController).
    /// Pulls a PDF off a public/shared Google Drive link and hands back the raw bytes.
    /// Kept deliberately separate from every other service in this project — this
    /// feature has its own controller, own storage folder, and its own service so it
    /// can be built/deployed/iterated on without touching the rest of the panel.
    /// </summary>
    public class GoogleDrivePdfService
    {
        private readonly HttpClient _http;
        private readonly ILogger<GoogleDrivePdfService> _logger;

        public GoogleDrivePdfService(HttpClient http, ILogger<GoogleDrivePdfService> logger)
        {
            _http = http;
            _logger = logger;
        }

        // Matches, in order: "/file/d/{id}/...", "?id={id}" or "&id={id}", or a bare file id.
        private static readonly Regex[] IdPatterns =
        {
            new Regex(@"/file/d/([a-zA-Z0-9_-]+)", RegexOptions.Compiled),
            new Regex(@"[?&]id=([a-zA-Z0-9_-]+)", RegexOptions.Compiled),
            new Regex(@"^([a-zA-Z0-9_-]{15,})$", RegexOptions.Compiled),
        };

        public static string? ExtractFileId(string driveLinkOrId)
        {
            if (string.IsNullOrWhiteSpace(driveLinkOrId)) return null;
            driveLinkOrId = driveLinkOrId.Trim();

            foreach (var pattern in IdPatterns)
            {
                var m = pattern.Match(driveLinkOrId);
                if (m.Success) return m.Groups[1].Value;
            }
            return null;
        }

        /// <summary>
        /// Downloads the file bytes for a Drive file id, following Google's
        /// "can't scan this file for viruses" confirmation redirect for larger files.
        /// The Drive file must be shared as "Anyone with the link" — a private file
        /// will come back as an HTML sign-in page and this will throw.
        ///
        /// FileName is whatever Google sent back in the download response's
        /// Content-Disposition header — i.e. the file's actual name in Drive — so the
        /// caller can save it under that name instead of a generated one. Null if Drive
        /// didn't send a name (rare; caller should fall back to a generated name).
        /// </summary>
        public async Task<(byte[] Bytes, string ContentType, string? FileName)> DownloadAsync(string fileId, CancellationToken ct = default)
        {
            // Modern endpoint — works for most publicly-shared files, including
            // ones that would otherwise show Google's virus-scan warning page.
            var (bytes, contentType, fileName) = await GetAsync(
                $"https://drive.usercontent.google.com/download?id={fileId}&export=download&confirm=t", ct);

            if (LooksLikeHtml(bytes, contentType))
            {
                // Fallback: classic uc?export=download flow, scraping the confirm token
                // out of the interstitial page (needed for some older/large files).
                var (confirmBytes, confirmType, confirmFileName) = await GetAsync(
                    $"https://drive.google.com/uc?export=download&id={fileId}", ct);

                if (!LooksLikeHtml(confirmBytes, confirmType))
                {
                    bytes = confirmBytes;
                    contentType = confirmType;
                    fileName = confirmFileName;
                }
                else
                {
                    var html = System.Text.Encoding.UTF8.GetString(confirmBytes);
                    var tokenMatch = Regex.Match(html, @"confirm=([0-9A-Za-z_-]+)");
                    if (!tokenMatch.Success)
                    {
                        throw new InvalidOperationException(
                            "Google Drive se file download nahi ho payi — link 'Anyone with the link' pe shared hona chahiye.");
                    }

                    (bytes, contentType, fileName) = await GetAsync(
                        $"https://drive.google.com/uc?export=download&confirm={tokenMatch.Groups[1].Value}&id={fileId}", ct);
                }
            }

            if (LooksLikeHtml(bytes, contentType))
            {
                throw new InvalidOperationException(
                    "Drive ne PDF ke bajaye HTML page bheja — link public share nahi hai ya file id galat hai.");
            }

            return (bytes, contentType, fileName);
        }

        private async Task<(byte[], string, string?)> GetAsync(string url, CancellationToken ct)
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var cd = resp.Content.Headers.ContentDisposition;
            var fileName = (cd?.FileNameStar ?? cd?.FileName)?.Trim('"');
            return (bytes, contentType, fileName);
        }

        /// <summary>
        /// Turns a Drive-supplied file name into something safe to use as a filesystem
        /// path segment: strips any directory-ish prefix, replaces characters that are
        /// invalid in file names, and guarantees a .pdf extension. Falls back to a
        /// generated name if Drive didn't send one.
        /// </summary>
        public static string SanitizeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Guid.NewGuid().ToString("N") + ".pdf";

            name = name.Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..].Trim();

            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            if (string.IsNullOrWhiteSpace(name))
                return Guid.NewGuid().ToString("N") + ".pdf";

            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                name += ".pdf";

            return name;
        }

        private static bool LooksLikeHtml(byte[] bytes, string contentType)
        {
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase)) return true;
            if (bytes.Length < 20) return false;
            var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 200)).TrimStart();
            return head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        }
    }
}
